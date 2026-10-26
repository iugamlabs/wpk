using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Testing;
using Wpk.Cli;
using Wpk.Cli.Platform;
using Wpk.Cli.Presentation;
using Wpk.Core;

namespace Wpk.Tests;

public sealed class PresentationTests
{
    [Theory]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(120)]
    public void TablesAdaptToWidthAndKeepChineseAndLiteralMarkup(int width)
    {
        using var console = new TestConsole().Width(width).SupportsUnicode(true);
        using var error = new TestConsole();
        var renderer = new SpectreRenderer(console, error);
        var item = Fixtures.Instance(Fixtures.Winget() with { Name = "[red]中文软件[/]" });
        renderer.List([item], false);
        Assert.Contains("中文软件", console.Output);
        Assert.Contains("[red]", console.Output);
        Assert.DoesNotContain(item.Id, console.Output);
        Assert.DoesNotContain('\u001b', console.Output);
    }

    [Fact]
    public void VerboseListsAndDetailsExposeIdsWithoutLosingLongPaths()
    {
        using var console = new TestConsole().Width(120);
        using var error = new TestConsole();
        var renderer = new SpectreRenderer(console, error);
        var item = Fixtures.Instance(Fixtures.Winget() with { Name = "中文软件", DirectoryPath = @"C:\Program Files\软件\long folder name\current" });
        renderer.List([item], true);
        renderer.Details(item, true);
        Assert.Contains(item.Id, console.Output);
        Assert.Contains("long folder name", console.Output);
        Assert.Contains("安装归属", console.Output);
        Assert.Contains("管理器识别", console.Output);
    }

    [Fact]
    public void NarrowDetailsWithLongUntrustedTextRenderWithoutMarkupExecution()
    {
        using var console = new TestConsole().Width(32).SupportsUnicode(true);
        using var error = new TestConsole();
        var renderer = new SpectreRenderer(console, error);
        var record = Fixtures.Record() with { Name = "[red]中文软件", DirectoryPath = @"C:\" + new string('a', 200) };
        renderer.Details(Fixtures.Instance(record), false);
        Assert.Contains("[red]", console.Output);
        Assert.DoesNotContain('\u001b', console.Output);
    }

    [Fact]
    public void DiagnosticsSummarizeSourcesButNeverHideBlockingErrors()
    {
        using var console = new TestConsole().Width(180);
        using var error = new TestConsole().Width(180);
        var renderer = new SpectreRenderer(console, error);
        Diagnostic[] diagnostics = [new("winget", "sourceAgreementRequired", new string('x', 300), Repository: "msstore"),
            new("upgrade", "ownershipConflict", "安装归属证据冲突", DiagnosticSeverity.Error)];
        renderer.Diagnostics(diagnostics, false, false);
        Assert.Contains("msstore", error.Output);
        Assert.Contains("wpk doctor", error.Output);
        Assert.Contains("ownershipConflict", error.Output);
        Assert.DoesNotContain("sourceAgreementRequired", error.Output);
        renderer.Diagnostics(diagnostics, true, false);
        Assert.Contains("sourceAgreementRequired", error.Output);
    }

    [Fact]
    public void NoColorAndRedirectedContextsNeverEmitAnsi()
    {
        foreach (var terminal in new[] { false, true })
        {
            var output = new StringWriter(); var error = new StringWriter();
            var context = new TerminalContext(output, error, terminal, false, noColor: true);
            new SpectreRenderer(context.Output, context.Error).List([Fixtures.Instance(Fixtures.Winget())], false);
            Assert.False(context.Output.Profile.Capabilities.Ansi);
            Assert.False(context.Interactive);
            Assert.DoesNotContain('\u001b', output.ToString());
        }
    }

    [Fact]
    public async Task NonAnimatedStatusRunsTheActionOnceWithoutWritingToStdout()
    {
        using var console = new TestConsole(); using var error = new TestConsole();
        var renderer = new SpectreRenderer(console, error);
        var calls = 0;
        Assert.Equal(42, await renderer.StatusAsync("正在读取", () => { calls++; return Task.FromResult(42); }, true));
        Assert.Equal(1, calls); Assert.Equal("", console.Output);
    }

    [Fact]
    public async Task SpectreSelectorSupportsCancelAndActualCandidateInput()
    {
        var item = Fixtures.Instance(Fixtures.Scoop());
        using var cancelConsole = new TestConsole().Interactive();
        cancelConsole.Input.PushKey(ConsoleKey.Enter);
        Assert.Null(await new SpectreInteraction(cancelConsole, true).SelectAsync([item], default));
        using var console = new TestConsole().Interactive().Width(160);
        console.Input.PushKey(ConsoleKey.DownArrow); console.Input.PushKey(ConsoleKey.Enter);
        var selected = await new SpectreInteraction(console, true).SelectAsync([item], default);
        Assert.Equal(item.Id, selected!.Id);
        Assert.Contains("归属", console.Output);
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    public async Task SpectreConfirmationDefaultsToNo(string input, bool expected)
    {
        using var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter(input);
        Assert.Equal(expected, await new SpectreInteraction(console, true).ConfirmUpgradeAsync(Preview(), default));
    }

    [Fact]
    public async Task ExhaustedInputCannotAuthorizeAnUpgrade()
    {
        using var console = new TestConsole().Interactive();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        Assert.False(await new SpectreInteraction(console, true).ConfirmUpgradeAsync(Preview(), cancellation.Token));
    }

    [Theory]
    [InlineData("--help", "--json")]
    [InlineData("--json")]
    [InlineData("update", "--help", "--json")]
    [InlineData("--version", "--json")]
    [InlineData("update", "--bad", "--json")]
    [InlineData("ls", "--", "unexpected", "--json")]
    [InlineData("doctor", "--", "unexpected", "--json")]
    public async Task FrameworkHelpAndErrorsUsePureJsonEnvelopes(params string[] args)
    {
        // Put the transport option before '--'; after it, '--json' is literal input.
        var transportArgs = args.Contains("--") ? new[] { "--json" }.Concat(args.Where(a => a != "--json")).ToArray() : args;
        var output = new StringWriter(); var error = new StringWriter(); var inventory = new FakeInventory(Fixtures.Snapshot());
        var app = new CliApplication(inventory, [], new FakeRunner(), new FakeOpener(), output, error);
        await app.RunAsync(transportArgs);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.DoesNotContain('\u001b', output.ToString());
        Assert.Equal("", error.ToString()); Assert.Equal(0, inventory.Calls);
    }

    [Fact]
    public async Task OptionsBeforeCommandsAndLiteralFlagsRetainTheirMeaning()
    {
        CliOptions? parsed = null;
        Task<int> Capture(CliOptions options, CancellationToken _) { parsed = options; return Task.FromResult(0); }
        await CommandLine.InvokeAsync(["--json", "--scope", "user", "update", "Typora"], Capture, default);
        Assert.Equal("up", parsed!.Command); Assert.True(parsed.Json); Assert.Equal(InstallScope.User, parsed.Scope);
        await CommandLine.InvokeAsync(["find", "before", "--", "--json", "--help"], Capture, default);
        Assert.Equal("before --json --help", parsed!.Query); Assert.False(parsed.Json);
    }

    [Fact]
    public async Task RepeatedBooleanFlagsFollowFrameworkIdempotentSemantics()
    {
        var output = new StringWriter();
        var app = new CliApplication(new FakeInventory(Fixtures.Snapshot()), [], new FakeRunner(), new FakeOpener(), output, new StringWriter());
        Assert.Equal(0, await app.RunAsync(["ls", "--json", "--json"]));
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("--version")]
    public async Task BothFrameworkVersionFlagsUseTheVersionJsonField(string flag)
    {
        var output = new StringWriter();
        var app = new CliApplication(new FakeInventory(Fixtures.Snapshot()), [], new FakeRunner(), new FakeOpener(), output, new StringWriter());
        Assert.Equal(0, await app.RunAsync([flag, "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("0.2.0", json.RootElement.GetProperty("data").GetProperty("version").GetString());
    }

    private static UpgradePreview Preview()
    {
        var record = Fixtures.Winget(); var item = Fixtures.Instance(record);
        return new(item, new(false, "confirmationRequired", "来源未知", record.UpgradeTarget),
            new("winget.exe", ["upgrade", "--id", "Vendor.App"]), true, true, false);
    }
}
