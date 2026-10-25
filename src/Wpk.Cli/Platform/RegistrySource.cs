using Microsoft.Win32;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed record UninstallEntry(string Key, InstallScope Scope, string View,
    IReadOnlyDictionary<string, object?> Values);

public sealed class RegistrySource : IInventorySource
{
    public string Name => "registry";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<SourceResult> CollectAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var records = new List<SoftwareRecord>();
        var diagnostics = new List<Diagnostic>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in Environment.Is64BitOperatingSystem
                     ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : new[] { RegistryView.Registry32 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath);
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        if (key is null) continue;
                        var values = key.GetValueNames().ToDictionary(n => n,
                            n => key.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames), StringComparer.OrdinalIgnoreCase);
                        var entry = new UninstallEntry(name,
                            hive == RegistryHive.CurrentUser ? InstallScope.User : InstallScope.Machine, view.ToString(), values);
                        var record = Map(entry);
                        if (record is not null) records.Add(record);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                    { diagnostics.Add(new(Name, "entryUnavailable", $"{hive}/{view}/{name}: {e.Message}")); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { diagnostics.Add(new(Name, "viewUnavailable", $"{hive}/{view}: {e.Message}")); }
        }
        return new SourceResult(records, diagnostics, diagnostics.Count > 0);
    }, cancellationToken);

    public static SoftwareRecord? Map(UninstallEntry entry)
    {
        string? Get(string name) => entry.Values.TryGetValue(name, out var value) && value is string s
            && !string.IsNullOrWhiteSpace(s) ? s : null;
        var name = Get("DisplayName");
        if (name is null || entry.Values.TryGetValue("SystemComponent", out var system) && Equals(system, 1)) return null;
        var directory = Identity.PathKey(Get("InstallLocation"));
        var productCode = Get("ProductCode");
        if (productCode is null && Guid.TryParse(entry.Key, out var guid)) productCode = guid.ToString("B").ToUpperInvariant();
        var family = Get("PackageFamilyName");
        var id = Get("WinGetPackageIdentifier");
        var source = Get("WinGetSourceIdentifier");
        var portable = Get("UninstallString")?.Contains("winget", StringComparison.OrdinalIgnoreCase) == true;
        // These are WinGet's portable-installer markers, not the ordinary ARP source correlation.
        var managed = id is not null && source is not null && directory is not null && portable;
        var evidence = new List<Evidence> { new("uninstallRecord", $"{entry.Scope}/{entry.View}/{UninstallPath}/{entry.Key}", "Windows 卸载注册表记录；不证明原始安装渠道。") };
        if (managed) evidence.Add(new("winGetPortableMarker", evidence[0].Location,
            $"WinGet portable 标记：{id}；source identifier={source}；须由 WinGet 当前关联交叉验证。"));
        return new()
        {
            Id = Identity.RecordId("registry", entry.Scope.ToString(), entry.View, entry.Key),
            Source = SourceKind.Registry, NativeId = entry.Key, Name = name,
            Version = Get("DisplayVersion"), Publisher = Get("Publisher"), Scope = entry.Scope,
            RegistryView = entry.View, DirectoryPath = directory,
            PortableMarker = managed ? new(id!, source!) : null,
            CanonicalDirectory = ResolveDirectory(directory),
            ProductCodes = productCode is null ? [] : [productCode],
            PackageFamilyNames = family is null ? [] : [family],
            Evidence = evidence,
            // Marker data is deliberately only evidence here. Promotion requires an exact API match.
            Aliases = []
        };
    }

    internal static string? ResolveDirectory(string? directory)
    {
        if (directory is null) return null;
        try { return new DirectoryInfo(directory).ResolveLinkTarget(true)?.FullName ?? directory; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return directory; }
    }
}
