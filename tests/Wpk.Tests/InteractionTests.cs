using System.Text.Json;
using Wpk.Cli;
using Wpk.Cli.Presentation;
using Wpk.Core;

namespace Wpk.Tests;

public sealed class InteractionTests
{
    private static SoftwareInstance Typora() => Fixtures.Instance(Fixtures.Winget() with
    { Name = "Typora", NativeId = "appmakes.Typora", UpgradeTarget = new(PackageManagerKind.WinGet, "appmakes.Typora", InstallScope.User, "winget") });

    [Theory]
    [InlineData("up")]
    [InlineData("update")]
    [InlineData("upgrade")]
    public async Task TyporaAliasesResolveFullNativeIdAndAskOnce(string alias)
    {
        var item = Typora(); var c = Create(Fixtures.Snapshot(item));
        c.Interaction.Confirm = true;
        c.Interaction.BeforeConfirm = () => Assert.Equal(0, c.Runner.Calls);
        Assert.Equal(0, await c.App.RunAsync([alias, "typora"]));
        Assert.Equal(1, c.Interaction.Confirmations);
        Assert.Equal(2, c.Inventory.Calls);
        Assert.Contains("appmakes.Typora", c.Runner.LastCommand!.Arguments);
        Assert.Empty(item.Owners);
        Assert.True(c.Interaction.Preview!.OriginUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonInteractiveUnknownOriginRequiresVia(bool json)
    {
        var c = Create(Fixtures.Snapshot(Typora()), interactive: false);
        var args = json ? new[] { "update", "typora", "--json" } : ["update", "typora"];
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(args));
        Assert.Equal(0, c.Runner.Calls);
        Assert.Equal(0, c.Interaction.Confirmations);
        c = Create(Fixtures.Snapshot(Typora()), interactive: false);
        Assert.Equal(0, await c.App.RunAsync(["update", "typora", "--via", "winget", "--json"]));
        Assert.Equal(0, c.Interaction.Confirmations);
    }

    [Fact]
    public async Task JsonSuppressesPromptsEvenIfTerminalIsInteractive()
    {
        var c = Create(Fixtures.Snapshot(Typora()));
        c.Interaction.Confirm = true;
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["up", "typora", "--json"]));
        Assert.Equal(0, c.Interaction.Confirmations);
    }

    [Fact]
    public async Task KnownExactOwnerExecutesWithoutConfirmation()
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop())));
        Assert.Equal(0, await c.App.RunAsync(["up", "Example App"]));
        Assert.Equal(0, c.Interaction.Confirmations);
        Assert.Equal(1, c.Runner.Calls);
    }

    [Theory]
    [InlineData("Exam", true)]
    [InlineData("exapp", true)]
    [InlineData("Exam", false)]
    [InlineData("exapp", false)]
    public async Task PartialAndFuzzyTargetsRequireConsent(string query, bool confirm)
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop())));
        c.Interaction.Confirm = confirm;
        Assert.Equal(confirm ? 0 : ExitCodes.Cancelled, await c.App.RunAsync(["up", query]));
        Assert.Equal(1, c.Interaction.Confirmations);
        Assert.Equal(confirm ? 1 : 0, c.Runner.Calls);
    }

    [Fact]
    public async Task RefusingUnknownOriginNeverStartsNativeProcess()
    {
        var c = Create(Fixtures.Snapshot(Typora()));
        Assert.Equal(ExitCodes.Cancelled, await c.App.RunAsync(["update", "typora"]));
        Assert.Equal(0, c.Runner.Calls);
        Assert.Equal(1, c.Inventory.Calls);
    }

    [Fact]
    public async Task DryRunPreviewsPendingConsentWithoutPrompting()
    {
        var c = Create(Fixtures.Snapshot(Typora()));
        Assert.Equal(0, await c.App.RunAsync(["update", "typora", "--dry-run", "--json"]));
        Assert.Equal(0, c.Interaction.Confirmations);
        Assert.Equal(0, c.Runner.Calls);
        using var json = JsonDocument.Parse(c.Output.ToString());
        Assert.True(json.RootElement.GetProperty("data").GetProperty("confirmationRequired").GetBoolean());
        Assert.Equal("appmakes.Typora", json.RootElement.GetProperty("data").GetProperty("decision").GetProperty("target").GetProperty("packageId").GetString());
    }

    [Theory]
    [InlineData("where")]
    [InlineData("open")]
    [InlineData("up")]
    public async Task MultipleInstancesCanBeSelectedWithoutRepeatingNameConfirmation(string command)
    {
        var a = Fixtures.Instance(Fixtures.Scoop());
        var b = Fixtures.Instance(Fixtures.Scoop("scoop:two") with
        { Scope = InstallScope.Machine, UpgradeTarget = new(PackageManagerKind.Scoop, "app", InstallScope.Machine, "main", @"C:\Global"), DirectoryPath = @"D:\Other", CanonicalDirectory = @"D:\Other" });
        var c = Create(Fixtures.Snapshot(a, b));
        c.Interaction.SelectId = b.Id;
        Assert.Equal(0, await c.App.RunAsync([command, "Example App"]));
        Assert.Equal(1, c.Interaction.Selections);
        Assert.Equal(0, c.Interaction.Confirmations);
        if (command == "up") Assert.Equal(InstallScope.Machine, c.Scoop.LastTarget!.Scope);
        if (command == "open") Assert.Equal(@"D:\Other", c.Opener.Opened);
    }

    [Fact]
    public async Task CancelledSelectionDoesNotOpenOrUpgrade()
    {
        var c = Create(Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop()), Fixtures.Instance(Fixtures.Scoop("scoop:two"))));
        Assert.Equal(ExitCodes.Cancelled, await c.App.RunAsync(["up", "Example App"]));
        Assert.Equal(0, c.Runner.Calls); Assert.Null(c.Opener.Opened);
    }

    [Fact]
    public async Task ExactNameWinsOverLowerConfidenceResults()
    {
        var a = Fixtures.Instance(Fixtures.Scoop());
        var b = Fixtures.Instance(Fixtures.Scoop("scoop:two") with
        { Name = "Example App Pro", NativeId = "app-pro", UpgradeTarget = new(PackageManagerKind.Scoop, "app-pro", InstallScope.User, "main", @"C:\scoop") });
        var c = Create(Fixtures.Snapshot(a, b));
        Assert.Equal(0, await c.App.RunAsync(["up", "Example App", "--json"]));
        Assert.Equal(0, c.Interaction.Selections);
        Assert.Contains("app", c.Runner.LastCommand!.Arguments);
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("repository")]
    [InlineData("ownership")]
    [InlineData("incomplete")]
    [InlineData("nativeDuplicate")]
    public async Task ConfirmationCannotBypassStructuralGuards(string kind)
    {
        var record = Fixtures.Winget();
        var item = Fixtures.Instance(record);
        var snapshot = Fixtures.Snapshot(item);
        if (kind == "scope") snapshot = Fixtures.Snapshot(Fixtures.Instance(record with { UpgradeTarget = record.UpgradeTarget! with { Scope = InstallScope.Unknown } }));
        if (kind == "repository") snapshot = Fixtures.Snapshot(Fixtures.Instance(record, record with { Id = "winget:other", UpgradeTarget = record.UpgradeTarget! with { Repository = "other" } }));
        if (kind == "ownership") snapshot = Fixtures.Snapshot(Fixtures.Instance(Fixtures.Scoop(), record with { Owner = PackageManagerKind.WinGet }));
        if (kind == "incomplete") snapshot = snapshot with { Partial = true, Diagnostics = [new("registry", "denied", "denied")] };
        if (kind == "nativeDuplicate") snapshot = Fixtures.Snapshot(item, Fixtures.Instance(record with { Id = "winget:two" }));
        var c = Create(snapshot); c.Interaction.Confirm = true;
        // Select by observation ID to isolate manager-target guards from the interactive selector.
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["up", snapshot.Instances[0].Id]));
        Assert.Equal(0, c.Runner.Calls); Assert.Equal(0, c.Interaction.Confirmations);
    }

    [Fact]
    public async Task InventoryIsRevalidatedAfterUserConfirmation()
    {
        var before = Typora(); var after = before with { Records = [before.Records[0] with { Version = "2.0" }] };
        var c = Create(Fixtures.Snapshot(before), fresh: Fixtures.Snapshot(after)); c.Interaction.Confirm = true;
        Assert.Equal(ExitCodes.Undetermined, await c.App.RunAsync(["update", "typora"]));
        Assert.Equal(1, c.Interaction.Confirmations); Assert.Equal(0, c.Runner.Calls);
    }

    [Fact]
    public async Task NativeCommandPinsTyporaIdentity()
    {
        var c = Create(Fixtures.Snapshot(Typora()));
        var adapter = new Wpk.Cli.Platform.WinGetUpgradeAdapter(() => @"C:\Tools\winget.exe");
        var app = new CliApplication(c.Inventory, [adapter], c.Runner, c.Opener, c.Output, new StringWriter(), _ => true,
            userInteraction: c.Interaction);
        c.Interaction.Confirm = true;
        Assert.Equal(0, await app.RunAsync(["update", "typora"]));
        Assert.Equal(["upgrade", "--id", "appmakes.Typora", "--exact", "--source", "winget", "--scope", "user"], c.Runner.LastCommand!.Arguments);
    }

    private static Context Create(InventorySnapshot snapshot, bool interactive = true, InventorySnapshot? fresh = null)
    {
        var inventory = new FakeInventory(snapshot, fresh ?? snapshot); var runner = new FakeRunner(); var opener = new FakeOpener();
        var interaction = new FakeInteraction(); var output = new StringWriter(); var scoop = new FakeAdapter(PackageManagerKind.Scoop);
        var app = new CliApplication(inventory, [scoop, new FakeAdapter(PackageManagerKind.WinGet)], runner, opener,
            output, new StringWriter(), _ => true, interactive: interactive, userInteraction: interaction);
        return new(app, inventory, runner, opener, interaction, output, scoop);
    }
    private sealed record Context(CliApplication App, FakeInventory Inventory, FakeRunner Runner, FakeOpener Opener,
        FakeInteraction Interaction, StringWriter Output, FakeAdapter Scoop);
    private sealed class FakeInteraction : IUserInteraction
    {
        public bool IsInteractive => true;
        public bool Confirm { get; set; }
        public string? SelectId { get; set; }
        public int Confirmations { get; private set; }
        public int Selections { get; private set; }
        public Action? BeforeConfirm { get; set; }
        public UpgradePreview? Preview { get; private set; }
        public Task<SoftwareInstance?> SelectAsync(IReadOnlyList<SoftwareInstance> candidates, CancellationToken cancellationToken)
        { Selections++; return Task.FromResult(candidates.SingleOrDefault(i => i.Id == SelectId)); }
        public Task<bool> ConfirmUpgradeAsync(UpgradePreview preview, CancellationToken cancellationToken)
        { BeforeConfirm?.Invoke(); Confirmations++; Preview = preview; return Task.FromResult(Confirm); }
    }
}
