namespace Wpk.Core;

/// <summary>Uses mutually unique, compatible matches. Never uses fuzzy search as identity.</summary>
public sealed class InstanceMerger
{
    public IReadOnlyList<SoftwareInstance> Merge(IEnumerable<SoftwareRecord> observations)
    {
        var groups = observations.DistinctBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(r => new SoftwareInstance(r.Id, [r], [])).ToList();
        MergePass(groups, strong: true);
        MergePass(groups, strong: false);
        return groups.Select(CorroboratePortableOwnership)
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Id).ToArray();
    }

    private static void MergePass(List<SoftwareInstance> groups, bool strong)
    {
        while (true)
        {
            var edges = new List<(int A, int B, string Reason)>();
            var degree = new int[groups.Count];
            for (var a = 0; a < groups.Count; a++)
            for (var b = a + 1; b < groups.Count; b++)
            {
                if (!groups[a].Records.All(x => groups[b].Records.All(y => Compatible(x, y)))) continue;
                if (!strong && groups[a].Records.Select(r => r.Source).Intersect(groups[b].Records.Select(r => r.Source)).Any()) continue;
                var reason = groups[a].Records.SelectMany(x => groups[b].Records.Select(y =>
                    MatchReason(x, y, strong))).FirstOrDefault(x => x is not null);
                if (reason is null) continue;
                edges.Add((a, b, reason));
                degree[a]++; degree[b]++;
            }
            var edgeSet = edges.Select(e => (e.A, e.B)).ToHashSet();
            var edge = edges.FirstOrDefault(e =>
            {
                if (!strong) return degree[e.A] == 1 && degree[e.B] == 1;
                // A strong match may join a complete clique of three or more observations,
                // but cannot use one record as a bridge between incompatible installations.
                var neighbors = edges.Where(x => x.A == e.A || x.B == e.A || x.A == e.B || x.B == e.B)
                    .SelectMany(x => new[] { x.A, x.B }).Distinct().Order().ToArray();
                return neighbors.All(a => neighbors.Where(b => b > a).All(b => edgeSet.Contains((a, b))));
            });
            if (edge.Reason is null) return;
            var left = groups[edge.A];
            var right = groups[edge.B];
            var records = left.Records.Concat(right.Records).ToArray();
            var primary = new SoftwareInstance("", records, []).Primary;
            groups[edge.A] = new(primary.Id, records,
                left.MergeReasons.Concat(right.MergeReasons).Append(edge.Reason).Distinct().ToArray());
            groups.RemoveAt(edge.B);
        }
    }

    private static bool Compatible(SoftwareRecord a, SoftwareRecord b)
    {
        if (a.Scope != InstallScope.Unknown && b.Scope != InstallScope.Unknown && a.Scope != b.Scope) return false;
        if (PresentDifferent(a.Version, b.Version) || PresentDifferent(a.Architecture, b.Architecture)) return false;
        if (a.Publisher is not null && b.Publisher is not null
            && Identity.Text(a.Publisher) != Identity.Text(b.Publisher)) return false;
        var ap = Identity.PathKey(a.CanonicalDirectory ?? a.DirectoryPath);
        var bp = Identity.PathKey(b.CanonicalDirectory ?? b.DirectoryPath);
        if (PresentDifferent(ap, bp)) return false;
        if (a.ProductCodes.Count > 0 && b.ProductCodes.Count > 0 && !Overlap(a.ProductCodes, b.ProductCodes)) return false;
        if (a.PackageFamilyNames.Count > 0 && b.PackageFamilyNames.Count > 0
            && !Overlap(a.PackageFamilyNames, b.PackageFamilyNames)) return false;
        return true;
    }

    private static string? MatchReason(SoftwareRecord a, SoftwareRecord b, bool strong)
    {
        if (strong)
        {
            if (Overlap(a.ProductCodes, b.ProductCodes)) return "productCode";
            if (Overlap(a.PackageFamilyNames, b.PackageFamilyNames)) return "packageFamilyName";
            if (a.Source == b.Source && a.Source != SourceKind.Registry && Identity.Same(a.NativeId, b.NativeId)
                && a.Scope != InstallScope.Unknown && a.Scope == b.Scope) return "packageIdentity";
            var path = Identity.PathKey(a.CanonicalDirectory ?? a.DirectoryPath);
            if (path is not null && IsSpecificDirectory(path)
                && Identity.Same(path, Identity.PathKey(b.CanonicalDirectory ?? b.DirectoryPath))
                && NamesAgree(a, b)) return "installationDirectory";
            return null;
        }
        if (a.Source == b.Source || a.Scope == InstallScope.Unknown || a.Scope != b.Scope) return null;
        if (Identity.Text(a.Name) != Identity.Text(b.Name) || Identity.Text(a.Name).Length == 0) return null;
        if (Identity.Text(a.Publisher).Length == 0 || Identity.Text(a.Publisher) != Identity.Text(b.Publisher)) return null;
        return Identity.Same(a.Version, b.Version) ? "uniqueNamePublisherVersionScope" : null;
    }

    private static bool NamesAgree(SoftwareRecord a, SoftwareRecord b)
    {
        var names = new[] { a.Name, a.NativeId.Split('.').Last() }.Concat(a.Aliases).Select(Identity.Text)
            .Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        return new[] { b.Name, b.NativeId.Split('.').Last() }.Concat(b.Aliases).Select(Identity.Text)
            .Any(names.Contains);
    }

    private static bool IsSpecificDirectory(string path)
    {
        if (Identity.Same(path, Path.GetPathRoot(path))) return false;
        var excluded = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.CommonApplicationData };
        return !excluded.Any(f => Identity.Same(path, Identity.PathKey(Environment.GetFolderPath(f))));
    }

    private static bool PresentDifferent(string? a, string? b) => !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b) && !Identity.Same(a, b);
    private static bool Overlap(IEnumerable<string> a, IEnumerable<string> b) =>
        a.Intersect(b, StringComparer.OrdinalIgnoreCase).Any();

    private static SoftwareInstance CorroboratePortableOwnership(SoftwareInstance instance)
    {
        var records = instance.Records.Select(record =>
        {
            if (record.Source != SourceKind.WinGet || record.UpgradeTarget is null) return record;
            var marker = instance.Records.FirstOrDefault(r => r.PortableMarker is { } m
                && Identity.Same(m.PackageId, record.NativeId)
                && Identity.Same(m.SourceIdentifier, record.RepositoryIdentifier)
                && r.Scope != InstallScope.Unknown && r.Scope == record.Scope
                && Identity.Same(Identity.PathKey(r.CanonicalDirectory ?? r.DirectoryPath),
                    Identity.PathKey(record.CanonicalDirectory ?? record.DirectoryPath)));
            return marker is null ? record : record with
            {
                Owner = PackageManagerKind.WinGet,
                Evidence = record.Evidence.Append(new Evidence("managedInstallation", marker.Id,
                    "当前 WinGet 包身份、仓库、范围及目录与 portable 安装标记一致。")).ToArray()
            };
        }).ToArray();
        return instance with { Records = records };
    }
}
