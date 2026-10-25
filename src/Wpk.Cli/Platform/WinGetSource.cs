using Microsoft.Management.Deployment;
using Wpk.Core;

namespace Wpk.Cli.Platform;

public sealed record WinGetObservation(string PackageId, string Name, string? Version,
    string? Publisher, InstallScope Scope, string Repository, string RepositoryIdentifier,
    string? Directory, IReadOnlyList<string> ProductCodes, IReadOnlyList<string> PackageFamilyNames,
    string InstalledId);

public sealed class WinGetSource : IInventorySource
{
    public string Name => "winget";

    public async Task<SourceResult> CollectAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            // COM activation and projection loading stay on a worker; the caller has a bounded wait.
            return await Task.Run(() => CollectInternalAsync(deadline.Token), deadline.Token)
                .WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new([], [new(Name, "sourceTimeout", "WinGet 查询超过 25 秒，其他来源仍可使用。")], true); }
        catch (Exception e) when (e is not OperationCanceledException)
        { return new([], [new(Name, "apiUnavailable", $"WinGet COM API 不可用：{e.Message} (0x{e.HResult:X8})")], true); }
    }

    private async Task<SourceResult> CollectInternalAsync(CancellationToken cancellationToken)
    {
        var records = new List<SoftwareRecord>();
        var diagnostics = new List<Diagnostic>();
        var manager = new PackageManager();
        var catalogs = manager.GetPackageCatalogs();
        for (var index = 0; index < catalogs.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reference = catalogs[index];
            var name = reference.Info.Name;
            try
            {
                // Read the installed inventory with cached source correlation. Never accept agreements.
                reference.AcceptSourceAgreements = false;
                reference.PackageCatalogBackgroundUpdateInterval = TimeSpan.Zero;
                if (reference.SourceAgreements.Count > 0)
                {
                    diagnostics.Add(new(Name, "sourceAgreementRequired",
                        $"仓库 {name} 要求调用方显式接受协议；首版 WPK 不接受仓库协议，已跳过该仓库。CLI 中已有的协议确认不等于授予 WPK 确认。", Repository: name));
                    continue;
                }
                var options = new CreateCompositePackageCatalogOptions
                { CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs };
                options.Catalogs.Add(reference);
                var composite = manager.CreateCompositePackageCatalog(options);
                var connection = await composite.ConnectAsync().AsTask(cancellationToken);
                if (connection.Status != ConnectResultStatus.Ok)
                    throw new InvalidOperationException($"连接状态 {connection.Status}");
                var result = await connection.PackageCatalog.FindPackagesAsync(new FindPackagesOptions()).AsTask(cancellationToken);
                if (result.Status != FindPackagesResultStatus.Ok)
                    throw new InvalidOperationException($"查询状态 {result.Status}");
                if (result.WasLimitExceeded) diagnostics.Add(new(Name, "truncatedInventory", $"仓库 {name} 的已安装结果被截断。", Repository: name));
                for (var i = 0; i < result.Matches.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var package = result.Matches[i].CatalogPackage;
                        var installed = package.InstalledVersion;
                        if (installed is null || package.AvailableVersions.Count == 0) continue;
                        // An available version from this catalog proves recognition, not installation ownership.
                        var available = package.GetPackageVersionInfo(package.AvailableVersions[0]);
                        if (available is null || !Identity.Same(available.PackageCatalog.Info.Name, name)) continue;
                        var resolvedSourceId = available.PackageCatalog.Info.Id;
                        records.Add(Map(new(available.Id, installed.DisplayName, installed.Version,
                            EmptyToNull(installed.GetMetadata(PackageVersionMetadataField.PublisherDisplayName)),
                            ParseScope(installed.GetMetadata(PackageVersionMetadataField.InstalledScope)),
                            name, resolvedSourceId,
                            EmptyToNull(installed.GetMetadata(PackageVersionMetadataField.InstalledLocation)),
                            Copy(installed.ProductCodes), Copy(installed.PackageFamilyNames), installed.Id)));
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    { diagnostics.Add(new(Name, "packageUnavailable", $"{name}: {e.Message}", Repository: name)); }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { diagnostics.Add(new(Name, "catalogUnavailable", $"仓库 {name}: {e.Message}；请在终端检查该仓库；WPK 不自动接受仓库协议。", Repository: name)); }
        }
        if (catalogs.Count == 0) diagnostics.Add(new(Name, "noCatalogs", "WinGet 未配置仓库。"));
        return new(records, diagnostics, diagnostics.Count > 0);
    }

    public static SoftwareRecord Map(WinGetObservation item)
    {
        var path = Identity.PathKey(item.Directory);
        return new()
        {
            Id = Identity.RecordId("winget", item.RepositoryIdentifier, item.PackageId,
                item.Scope.ToString(), item.InstalledId, path,
                string.Join(';', item.ProductCodes.Order(StringComparer.OrdinalIgnoreCase))),
            Source = SourceKind.WinGet, NativeId = item.PackageId, Name = item.Name,
            Version = EmptyToNull(item.Version), Publisher = EmptyToNull(item.Publisher), Scope = item.Scope,
            RepositoryIdentifier = item.RepositoryIdentifier,
            DirectoryPath = path, CanonicalDirectory = RegistrySource.ResolveDirectory(path),
            ProductCodes = item.ProductCodes, PackageFamilyNames = item.PackageFamilyNames,
            UpgradeTarget = new(PackageManagerKind.WinGet, item.PackageId, item.Scope, item.Repository),
            Evidence = [new("catalogRecognition", item.RepositoryIdentifier,
                $"WinGet 仓库 {item.Repository} 将当前安装记录关联到 {item.PackageId}；不证明历史安装来源。")]
        };
    }

    private static string[] Copy(IReadOnlyList<string> values)
    {
        var result = new string[values.Count];
        for (var i = 0; i < values.Count; i++) result[i] = values[i];
        return result;
    }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static InstallScope ParseScope(string value) => value.ToLowerInvariant() switch
    { "user" => InstallScope.User, "machine" or "system" => InstallScope.Machine, _ => InstallScope.Unknown };
}
