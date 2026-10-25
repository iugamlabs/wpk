using System.Text.Json;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed record ScoopSettings(string UserRoot, string GlobalRoot, bool NoJunction = false)
{
    public static ScoopSettings Discover()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(profile, ".config");
        string? root = null, global = null;
        var noJunction = false;
        var config = Path.Combine(configHome, "scoop", "config.json");
        if (File.Exists(config))
        {
            // Invalid configuration is surfaced by startup; never silently scan another custom root.
            using var json = JsonDocument.Parse(File.ReadAllText(config));
            foreach (var p in json.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("root_path", StringComparison.OrdinalIgnoreCase)) root = p.Value.GetString();
                if (p.Name.Equals("global_path", StringComparison.OrdinalIgnoreCase)) global = p.Value.GetString();
                if (p.Name.Equals("no_junction", StringComparison.OrdinalIgnoreCase)) noJunction = p.Value.ValueKind == JsonValueKind.True;
            }
        }
        var shim = ExecutableLocator.Find("scoop.ps1");
        var shimRoot = shim is null ? null : Path.GetDirectoryName(Path.GetDirectoryName(shim));
        return new(Expand(Environment.GetEnvironmentVariable("SCOOP") ?? root ?? shimRoot ?? Path.Combine(profile, "scoop")),
            Expand(Environment.GetEnvironmentVariable("SCOOP_GLOBAL") ?? global
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "scoop")), noJunction);
    }

    private static string Expand(string path)
    {
        if (path.StartsWith('~')) path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..];
        return Identity.PathKey(path) ?? throw new InvalidDataException("Scoop 根目录不是有效的绝对路径。");
    }
}

public sealed class ScoopSource(ScoopSettings settings) : IInventorySource
{
    public string Name => "scoop";

    public Task<SourceResult> CollectAsync(CancellationToken cancellationToken) => Task.Run(() => Collect(cancellationToken), cancellationToken);

    private SourceResult Collect(CancellationToken cancellationToken)
    {
        var records = new List<SoftwareRecord>();
        var diagnostics = new List<Diagnostic>();
        var foundRoot = false;
        foreach (var (root, scope) in new[] { (settings.UserRoot, InstallScope.User), (settings.GlobalRoot, InstallScope.Machine) })
        {
            var apps = Path.Combine(root, "apps");
            if (!Directory.Exists(apps)) continue;
            foundRoot = true;
            try
            {
                foreach (var app in Directory.EnumerateDirectories(apps))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Path.GetFileName(app).Equals("scoop", StringComparison.OrdinalIgnoreCase)) continue;
                    try { records.Add(ReadApplication(app, root, scope)); }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
                    { diagnostics.Add(new(Name, "invalidInstallation", $"{app}: {e.Message}")); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new(Name, "rootUnavailable", $"{apps}: {e.Message}")); }
        }
        if (!foundRoot) diagnostics.Add(new(Name, "sourceUnavailable", "未找到 Scoop apps 目录。", DiagnosticSeverity.Info));
        return new(records, diagnostics, diagnostics.Count != 0);
    }

    private SoftwareRecord ReadApplication(string app, string root, InstallScope scope)
    {
        var current = Path.Combine(app, "current");
        if (!Directory.Exists(current))
        {
            if (!settings.NoJunction) throw new InvalidDataException("当前版本目录缺失或链接失效。");
            var versions = Directory.EnumerateDirectories(app).Where(p =>
                File.Exists(Path.Combine(p, "scoop-manifest.json")) || File.Exists(Path.Combine(p, "manifest.json"))).ToArray();
            if (versions.Length != 1) throw new InvalidDataException("NO_JUNCTION 模式下无法唯一确定活动版本。");
            current = versions[0];
        }
        var manifestPath = PreferredFile(current, "scoop-manifest.json", "manifest.json");
        var installPath = PreferredFile(current, "scoop-install.json", "install.json");
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var install = JsonDocument.Parse(File.ReadAllText(installPath));
        var m = manifest.RootElement;
        var metadata = install.RootElement;
        var version = String(m, "version") ?? throw new InvalidDataException("manifest 缺少 version。");
        var bucket = String(metadata, "bucket");
        var architecture = String(metadata, "architecture");
        if (bucket is null && architecture is null && String(metadata, "url") is null)
            throw new InvalidDataException("安装元数据缺少可验证的 Scoop 字段。");
        var name = Path.GetFileName(app);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (m.TryGetProperty("bin", out var bin)) ReadAliases(bin, aliases);
        return new()
        {
            Id = Identity.RecordId("scoop", root, name, scope.ToString()), Source = SourceKind.Scoop,
            NativeId = name, Name = name, Version = version, Scope = scope, Architecture = architecture,
            DirectoryPath = current, CanonicalDirectory = new DirectoryInfo(current).ResolveLinkTarget(true)?.FullName ?? current,
            Aliases = aliases.ToArray(), Owner = PackageManagerKind.Scoop,
            UpgradeTarget = new(PackageManagerKind.Scoop, name, scope, bucket, root),
            Evidence = [new("managedInstallation", installPath, $"Scoop 安装记录；bucket={bucket ?? "未知"}"),
                new("manifest", manifestPath, $"当前版本 {version}")]
        };
    }

    private static string PreferredFile(string path, string preferred, string legacy) =>
        File.Exists(Path.Combine(path, preferred)) ? Path.Combine(path, preferred) : Path.Combine(path, legacy);

    private static void ReadAliases(JsonElement bin, HashSet<string> aliases)
    {
        if (bin.ValueKind == JsonValueKind.String)
        {
            aliases.Add(Path.GetFileNameWithoutExtension(bin.GetString()!));
            return;
        }
        if (bin.ValueKind != JsonValueKind.Array) return;
        foreach (var entry in bin.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String) ReadAliases(entry, aliases);
            else if (entry.ValueKind == JsonValueKind.Array)
            {
                var values = entry.EnumerateArray().ToArray();
                if (values.Length > 0 && values[0].ValueKind == JsonValueKind.String) ReadAliases(values[0], aliases);
                if (values.Length > 1 && values[1].ValueKind == JsonValueKind.String) aliases.Add(values[1].GetString()!);
            }
        }
    }

    internal static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
}
