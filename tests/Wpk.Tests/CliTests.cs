using System.Text.Json;
using Wpk.Cli;
using Wpk.Core;

namespace Wpk.Tests;

public sealed class CliTests
{
    [Theory]
    [InlineData("ls", "--via", "winget")]
    [InlineData("where", "app", "--dry-run")]
    [InlineData("up", "app", "--via", "unknown")]
    [InlineData("ls", "--scope", "other")]
    [InlineData("ls", "extra")]
    [InlineData("find")]
    [InlineData("doctor", "--scope", "user")]
    [InlineData("ls", "--json", "--json")]
    [InlineData("ls", "--unknown")]
    public void InvalidArgumentsAreRejected(params string[] args) => Assert.Throws<ArgumentException>(() => CommandLine.Parse(args));

    [Fact]
    public void ParserHandlesMultiwordQueriesAndOptionTerminator()
    {
        Assert.Equal("Visual Studio Code", CommandLine.Parse(["find", "Visual", "Studio", "Code"]).Query);
        Assert.Equal("--literal", CommandLine.Parse(["find", "--", "--literal"]).Query);
        Assert.Equal("query", CommandLine.Parse(["app"]).Command);
        Assert.Equal("help", CommandLine.Parse([]).Command);
    }

    [Fact]
    public async Task HelpAndVersionDoNotCollectInventory()
    {
        var context = Create();
        Assert.Equal(0, await context.App.RunAsync(["--help", "--json"]));
        Assert.Equal(0, context.Inventory.Calls);
        using var json = JsonDocument.Parse(context.Output.ToString());
        Assert.Contains("--dry-run", json.RootElement.GetProperty("data").GetProperty("help").GetString());
    }

    [Theory]
    [InlineData("where")]
    [InlineData("open")]
    [InlineData("up")]
    public async Task MultipleMatchesNeverChooseHighestRank(string command)
    {
        var one = Fixtures.Instance(Fixtures.Scoop());
        var two = Fixtures.Instance(Fixtures.Scoop("scoop:two") with { Name = "Example App Pro" });
        var c = Create(Fixtures.Snapshot(one, two));
        Assert.Equal(ExitCodes.Ambiguous, await c.App.RunAsync([command, "Example App", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
        Assert.Null(c.Opener.Opened);
    }

    [Theory]
    [InlineData("find")]
    [InlineData("where")]
    [InlineData("open")]
    [InlineData("up")]
    public async Task MissingSoftwareHasDistinctExitCode(string command)
    {
        var c = Create();
        Assert.Equal(ExitCodes.NotFound, await c.App.RunAsync([command, "missing", "--json"]));
    }

    [Fact]
    public async Task DryRunReturnsCommandWithoutStartingProcessOrRescanning()
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop())));
        Assert.Equal(0, await c.App.RunAsync(["up", "scoop:one", "--dry-run", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
        Assert.Equal(1, c.Inventory.Calls);
        using var json = JsonDocument.Parse(c.Output.ToString());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("dryRun").GetBoolean());
        Assert.Equal("fake-manager.exe", json.RootElement.GetProperty("data").GetProperty("command").GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task UnknownOriginNeedsExplicitViaEvenForDryRun()
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Winget())));
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["up", "Vendor.App", "--dry-run", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
        c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Winget())));
        Assert.Equal(0, await c.App.RunAsync(["up", "Vendor.App", "--via", "winget", "--dry-run", "--json"]));
    }

    [Fact]
    public async Task JsonUpgradeCapturesNativeStreamsAndOriginalFailureCode()
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop())));
        c.Runner.Result = new(42, "manager says hello\n", "native error\n");
        Assert.Equal(ExitCodes.ExternalFailure, await c.App.RunAsync(["up", "scoop:one", "--json"]));
        Assert.Equal(2, c.Inventory.Calls);
        Assert.False(c.Runner.StreamOutput);
        Assert.True(c.Adapter.NonInteractive);
        Assert.Equal("", c.Error.ToString());
        using var json = JsonDocument.Parse(c.Output.ToString());
        var process = json.RootElement.GetProperty("data").GetProperty("process");
        Assert.Equal(42, process.GetProperty("exitCode").GetInt32());
        Assert.Equal("manager says hello\n", process.GetProperty("standardOutput").GetString());
    }

    [Fact]
    public async Task ChangedInstallationBetweenScanAndExecutionIsRejected()
    {
        var before = Fixtures.Instance(Fixtures.Scoop());
        var after = Fixtures.Instance(Fixtures.Scoop() with { Version = "2.0" });
        var c = Create(Fixtures.Snapshot(before), Fixtures.Snapshot(after));
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["up", "scoop:one", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
        Assert.Contains("installationChanged", c.Output.ToString());
    }

    [Fact]
    public async Task ArchitectureChangeAlsoInvalidatesTheUpgradePlan()
    {
        var before = Fixtures.Instance(Fixtures.Scoop() with { Architecture = "64bit" });
        var after = Fixtures.Instance(Fixtures.Scoop() with { Architecture = "32bit" });
        var c = Create(Fixtures.Snapshot(before), Fixtures.Snapshot(after));
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["up", "scoop:one", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
    }

    [Fact]
    public async Task DoctorProducesStructuredHealthAndDistinctFailureStatus()
    {
        var output = new StringWriter();
        var app = new CliApplication(new FakeInventory(Fixtures.Snapshot()), [], new FakeRunner(), new FakeOpener(),
            output, new StringWriter(), doctor: (_, _) => Task.FromResult(new Wpk.Cli.Platform.DoctorReport(
                [new("wingetCli", "warning", "unavailable")], 0)));
        Assert.Equal(ExitCodes.Unavailable, await app.RunAsync(["doctor", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("warning", json.RootElement.GetProperty("data").GetProperty("checks")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task IncompleteRelevantSourceCannotBeBypassed()
    {
        var snapshot = Fixtures.Snapshot(Fixtures.Instance(Fixtures.Winget())) with
        { Partial = true, Diagnostics = [new("registry", "denied", "access denied")] };
        var c = Create(snapshot);
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["up", "Vendor.App", "--via", "winget", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
        Assert.Contains("incompleteInventory", c.Output.ToString());
    }

    [Fact]
    public async Task ReadOnlyPartialInventoryStillProducesValidJson()
    {
        var snapshot = Fixtures.Snapshot(Fixtures.Instance(Fixtures.Record())) with
        { Partial = true, Diagnostics = [new("winget", "apiUnavailable", "unavailable")] };
        var c = Create(snapshot);
        Assert.Equal(0, await c.App.RunAsync(["ls", "--json"]));
        using var json = JsonDocument.Parse(c.Output.ToString());
        Assert.True(json.RootElement.GetProperty("partial").GetBoolean());
        Assert.Single(json.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task UnrelatedRepositoryFailureDoesNotBlockAnExactPinnedUpgrade()
    {
        var snapshot = Fixtures.Snapshot(Fixtures.Instance(Fixtures.Winget())) with
        { Partial = true, Diagnostics = [new("winget", "catalogUnavailable", "agreement not accepted", Repository: "msstore")] };
        var c = Create(snapshot);
        Assert.Equal(0, await c.App.RunAsync(["up", "Vendor.App", "--via", "winget", "--dry-run", "--json"]));
        Assert.Equal(0, c.Runner.Calls);
    }

    [Theory]
    [InlineData("where", false)]
    [InlineData("open", true)]
    public async Task LocateUsesKnownDirectoryAndOnlyOpenInvokesShell(string command, bool shouldOpen)
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Record())));
        Assert.Equal(0, await c.App.RunAsync([command, "registry:one"]));
        Assert.Equal(shouldOpen ? @"C:\Apps\Example" : null, c.Opener.Opened);
        Assert.Equal(0, c.Runner.Calls);
    }

    [Fact]
    public async Task UnknownAndMissingDirectoriesAreDistinctFailures()
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Record() with { DirectoryPath = null, CanonicalDirectory = null })));
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["where", "registry:one", "--json"]));
        var output = new StringWriter();
        var app = new CliApplication(new FakeInventory(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Record()))), [],
            new FakeRunner(), new FakeOpener(), output, new StringWriter(), _ => false);
        Assert.Equal(ExitCodes.Unavailable, await app.RunAsync(["open", "registry:one", "--json"]));
    }

    [Fact]
    public async Task StaleKnownDirectoryCannotTriggerAnUpgrade()
    {
        var runner = new FakeRunner();
        var output = new StringWriter();
        var app = new CliApplication(new FakeInventory(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop()))),
            [new FakeAdapter(PackageManagerKind.Scoop)], runner, new FakeOpener(), output, new StringWriter(), _ => false);
        Assert.Equal(ExitCodes.Undetermined, await app.RunAsync(["up", "scoop:one", "--json"]));
        Assert.Equal(0, runner.Calls);
        Assert.Contains("installationDirectoryMissing", output.ToString());
    }

    [Fact]
    public async Task JsonUsageErrorsAndCancellationHaveEnvelopes()
    {
        var c = Create();
        Assert.Equal(ExitCodes.Usage, await c.App.RunAsync(["up", "--json"]));
        using var json = JsonDocument.Parse(c.Output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        c = Create();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Equal(ExitCodes.Cancelled, await c.App.RunAsync(["ls", "--json"], cancellation.Token));
    }

    private static Context Create(params InventorySnapshot[] snapshots)
    {
        var source = new FakeInventory(snapshots.Length == 0 ? [Fixtures.Snapshot()] : snapshots);
        var runner = new FakeRunner(); var opener = new FakeOpener(); var adapter = new FakeAdapter(PackageManagerKind.Scoop);
        var output = new StringWriter(); var error = new StringWriter();
        var app = new CliApplication(source, [adapter, new FakeAdapter(PackageManagerKind.WinGet)], runner, opener, output, error, _ => true);
        return new(app, source, runner, opener, adapter, output, error);
    }

    private sealed record Context(CliApplication App, FakeInventory Inventory, FakeRunner Runner, FakeOpener Opener,
        FakeAdapter Adapter, StringWriter Output, StringWriter Error);
}
