using System.Runtime.InteropServices;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed record DoctorCheck(string Name, string Status, string Detail);
public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks, int InstalledInstances);

public sealed class EnvironmentDoctor(IProcessRunner runner, ScoopSettings? scoop)
{
    public async Task<DoctorReport> InspectAsync(InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>
        {
            new("windows", OperatingSystem.IsWindows() ? "ok" : "error", RuntimeInformation.OSDescription),
            new("dotnet", "ok", $"{Environment.Version} / {RuntimeInformation.ProcessArchitecture}")
        };
        if (scoop is not null)
        {
            checks.Add(new("scoopUserRoot", Directory.Exists(Path.Combine(scoop.UserRoot, "apps")) ? "ok" : "warning", scoop.UserRoot));
            checks.Add(new("scoopGlobalRoot", Directory.Exists(Path.Combine(scoop.GlobalRoot, "apps")) ? "ok" : "info", scoop.GlobalRoot));
            var script = Path.Combine(scoop.UserRoot, "shims", "scoop.ps1");
            checks.Add(new("scoopScript", File.Exists(script) ? "ok" : "warning", script));
        }
        checks.Add(new("powershell", ExecutableLocator.Find("pwsh.exe") is not null || ExecutableLocator.Find("powershell.exe") is not null ? "ok" : "warning",
            ExecutableLocator.Find("pwsh.exe") ?? ExecutableLocator.Find("powershell.exe") ?? "未找到"));
        var winget = ExecutableLocator.Find("winget.exe");
        if (winget is null) checks.Add(new("wingetCli", "warning", "找不到 winget.exe，请检查 App Installer 和执行别名。"));
        else
        {
            try
            {
                var result = await runner.RunAsync(new(winget, ["--version"]), TimeSpan.FromSeconds(5), false, cancellationToken);
                checks.Add(new("wingetCli", result.ExitCode == 0 && !result.TimedOut ? "ok" : "warning",
                    $"{winget}: {result.StandardOutput.Trim()} {result.StandardError.Trim()} exit={result.ExitCode}"));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { checks.Add(new("wingetCli", "warning", e.Message)); }
        }
        foreach (var source in new[] { "scoop", "winget", "registry" })
        {
            var diagnostics = snapshot.Diagnostics.Where(d => d.Source == source).ToArray();
            checks.Add(new(source + "Inventory", diagnostics.Length == 0 ? "ok" : "warning",
                diagnostics.Length == 0 ? "采集成功" : string.Join("；", diagnostics.Select(d => d.Message))));
        }
        return new(checks, snapshot.Instances.Count);
    }
}
