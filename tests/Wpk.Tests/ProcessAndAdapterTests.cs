using System.Text;
using System.Text.Json;
using Wpk.Cli.Platform;
using Wpk.Core;

namespace Wpk.Tests;

public sealed class ProcessAndAdapterTests
{
    private static string Shell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task ProcessArgumentsRemainLiteralAndBothStreamsAreDrained()
    {
        using var temp = new TempDirectory();
        var script = Path.Combine(temp.Root, "literal args.ps1");
        File.WriteAllText(script, "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n[Console]::Out.WriteLine((ConvertTo-Json -InputObject @($args) -Compress))\n[Console]::Error.Write('stderr value')\nexit 17", new UTF8Encoding(true));
        string[] arguments = ["space value", "a&b", "$(not executed)", "semi;colon", "quote\"value", "中文", @"C:\folder with spaces\"];
        var command = new CommandSpec(Shell, new[] { "-NoProfile", "-NonInteractive", "-File", script }.Concat(arguments).ToArray());
        var result = await new ProcessRunner().RunAsync(command, TimeSpan.FromSeconds(10), false, default);
        Assert.Equal(17, result.ExitCode);
        Assert.Equal("stderr value", result.StandardError);
        Assert.Equal(arguments, JsonSerializer.Deserialize<string[]>(result.StandardOutput.Trim()));
        Assert.False(result.TimedOut);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationAreReportedSeparately(bool cancel)
    {
        using var temp = new TempDirectory();
        var script = Path.Combine(temp.Root, "wait.ps1");
        File.WriteAllText(script, "Start-Sleep -Seconds 30");
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.CancelAfter(TimeSpan.FromMilliseconds(500));
        var result = await new ProcessRunner().RunAsync(new(Shell, ["-NoProfile", "-NonInteractive", "-File", script]),
            cancel ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(500), false, cancellation.Token);
        Assert.Equal(cancel, result.Cancelled);
        Assert.Equal(!cancel, result.TimedOut);
    }

    [Fact]
    public async Task LargeStdoutAndStderrCannotDeadlock()
    {
        using var temp = new TempDirectory();
        var script = Path.Combine(temp.Root, "output.ps1");
        File.WriteAllText(script, "$text = 'x' * 200000\n[Console]::Out.Write($text)\n[Console]::Error.Write($text)");
        var result = await new ProcessRunner().RunAsync(new(Shell, ["-NoProfile", "-NonInteractive", "-File", script]),
            TimeSpan.FromSeconds(10), false, default);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(200000, result.StandardOutput.Length);
        Assert.Equal(200000, result.StandardError.Length);
    }

    [Fact]
    public void WinGetCommandPinsIdentitySourceAndScopeWithoutAcceptingAgreements()
    {
        var command = new WinGetUpgradeAdapter(() => @"C:\tools\winget.exe")
            .BuildCommand(new(PackageManagerKind.WinGet, "Vendor.App", InstallScope.Machine, "private source"), true);
        Assert.Equal(["upgrade", "--id", "Vendor.App", "--exact", "--source", "private source", "--scope", "machine", "--disable-interactivity"], command.Arguments);
        Assert.DoesNotContain(command.Arguments, a => a.Contains("accept", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("app;whoami")]
    [InlineData("app & whoami")]
    [InlineData("$(whoami)")]
    public void ManagerIdsCannotBecomeFlagsOrShellCode(string id) => Assert.Throws<InvalidOperationException>(() =>
        new WinGetUpgradeAdapter(() => "winget.exe").BuildCommand(new(PackageManagerKind.WinGet, id, InstallScope.User, "winget"), true));

    [Fact]
    public void ScoopUsesFileInvocationAndExplicitRootsForGlobalUpgrade()
    {
        using var temp = new TempDirectory();
        var user = temp.Create("user root");
        var global = temp.Create("global root");
        var shim = temp.Create("user root/shims");
        File.WriteAllText(Path.Combine(shim, "scoop.ps1"), "# fake shim, never executed");
        var adapter = new ScoopUpgradeAdapter(new(user, global), () => Shell);
        var command = adapter.BuildCommand(new(PackageManagerKind.Scoop, "app", InstallScope.Machine, "main", global), true);
        Assert.Contains("-File", command.Arguments);
        Assert.DoesNotContain("-Command", command.Arguments);
        Assert.Equal("--global", command.Arguments[^1]);
        Assert.Equal(user, command.Environment!["SCOOP"]);
        Assert.Equal(global, command.Environment["SCOOP_GLOBAL"]);
        Assert.Throws<InvalidOperationException>(() => adapter.BuildCommand(new(PackageManagerKind.Scoop, "app", InstallScope.User, "main", global), false));
    }
}
