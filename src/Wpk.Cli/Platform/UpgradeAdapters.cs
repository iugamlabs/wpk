using System.Text.RegularExpressions;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed class WinGetUpgradeAdapter(Func<string?>? executable = null) : IUpgradeAdapter
{
    public PackageManagerKind Manager => PackageManagerKind.WinGet;

    public CommandSpec BuildCommand(UpgradeTarget target, bool nonInteractive)
    {
        AdapterValidation.Validate(target, Manager);
        if (string.IsNullOrWhiteSpace(target.Repository)) throw new InvalidOperationException("WinGet 仓库未知。");
        var path = (executable ?? (() => ExecutableLocator.Find("winget.exe")))()
            ?? throw new FileNotFoundException("找不到 winget.exe；请检查 App Installer 和 PATH。");
        var args = new List<string> { "upgrade", "--id", target.PackageId, "--exact", "--source", target.Repository,
            "--scope", target.Scope == InstallScope.User ? "user" : "machine" };
        if (nonInteractive) args.Add("--disable-interactivity");
        return new(path, args);
    }
}

public sealed class ScoopUpgradeAdapter(ScoopSettings settings, Func<string?>? powershell = null) : IUpgradeAdapter
{
    public PackageManagerKind Manager => PackageManagerKind.Scoop;

    public CommandSpec BuildCommand(UpgradeTarget target, bool nonInteractive)
    {
        AdapterValidation.Validate(target, Manager);
        var expectedRoot = target.Scope == InstallScope.User ? settings.UserRoot : settings.GlobalRoot;
        if (!Identity.Same(Identity.PathKey(target.Root), Identity.PathKey(expectedRoot)))
            throw new InvalidOperationException("Scoop 根目录与当前安装记录不一致。");
        var script = new[] { Path.Combine(settings.UserRoot, "shims", "scoop.ps1"),
            Path.Combine(settings.UserRoot, "apps", "scoop", "current", "bin", "scoop.ps1") }.FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException("找不到当前 Scoop 的 scoop.ps1。");
        var shell = (powershell ?? (() => ExecutableLocator.Find("pwsh.exe") ?? ExecutableLocator.Find("powershell.exe")))()
            ?? throw new FileNotFoundException("找不到 PowerShell。");
        var args = new List<string> { "-NoLogo", "-NoProfile" };
        if (nonInteractive) args.Add("-NonInteractive");
        args.AddRange(["-File", script, "update", target.PackageId]);
        if (target.Scope == InstallScope.Machine) args.Add("--global");
        return new(shell, args, new Dictionary<string, string>
        { ["SCOOP"] = settings.UserRoot, ["SCOOP_GLOBAL"] = settings.GlobalRoot });
    }
}

internal static partial class AdapterValidation
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+\\-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex PackageId();

    public static void Validate(UpgradeTarget target, PackageManagerKind expected)
    {
        if (target.Manager != expected || !PackageId().IsMatch(target.PackageId) || target.Scope == InstallScope.Unknown)
            throw new InvalidOperationException("管理器包身份或安装范围无效。");
    }
}
