namespace Wpk.Core;

public static class UpgradePlanner
{
    public static UpgradeDecision Decide(SoftwareInstance instance, PackageManagerKind? via,
        IReadOnlyList<SoftwareInstance> inventory)
    {
        var owners = instance.Owners;
        if (owners.Count > 1) return Deny("ownershipConflict", "安装归属证据冲突，无法选择升级管理器。");
        if (owners.Count == 1 && via.HasValue && owners[0] != via.Value)
            return Deny("ownershipConflict", "指定的管理器与已确认的安装归属冲突。");
        var candidates = instance.Records.Select(r => r.UpgradeTarget).OfType<UpgradeTarget>().Distinct().ToArray();
        if (candidates.Length == 0) return Deny("noUpgradeTarget", "没有可验证的管理器升级记录，请使用软件自身或官方安装程序升级。");
        if (owners.Count == 0 && via is null)
            return Deny("originUnknown", "安装来源未知；识别关系不代表安装归属。确认后可显式指定 --via winget。");
        var manager = via ?? owners[0];
        var targets = candidates.Where(c => c.Manager == manager).ToArray();
        if (targets.Length == 0) return Deny("managerNotRecognized", "当前实例没有指定管理器的可验证识别关系。");
        if (targets.Length != 1) return Deny("ambiguousUpgradeTarget", "存在多个管理器包或仓库匹配，无法保证唯一升级目标。");
        var target = targets[0];
        if (target.Scope == InstallScope.Unknown)
            return Deny("unknownScope", "安装范围未知，无法保证升级只影响当前实例。");
        if (target.Manager == PackageManagerKind.WinGet && string.IsNullOrWhiteSpace(target.Repository))
            return Deny("unknownRepository", "WinGet 仓库未知，无法精确升级。");
        if (target.Manager == PackageManagerKind.Scoop && owners.Count == 0)
            return Deny("unprovenScoop", "缺少 Scoop 安装归属证据。");
        if (inventory.Where(i => i.Id != instance.Id).SelectMany(i => i.Records).Any(r =>
            r.UpgradeTarget is { } other && other.Manager == target.Manager
            && Identity.Same(other.PackageId, target.PackageId)
            && (other.Scope == InstallScope.Unknown || other.Scope == target.Scope)
            && (target.Manager == PackageManagerKind.Scoop || Identity.Same(other.Repository, target.Repository))))
            return Deny("ambiguousNativeTarget", "另一个安装实例也会匹配此管理器命令，拒绝执行。");
        if (target.Manager == PackageManagerKind.WinGet && inventory.Where(i => i.Id != instance.Id)
            .SelectMany(i => i.Records).Any(other => other.Source == SourceKind.Registry
                && (other.Scope == InstallScope.Unknown || other.Scope == target.Scope)
                && instance.Records.Any(r => r.ProductCodes.Intersect(other.ProductCodes, StringComparer.OrdinalIgnoreCase).Any()
                    || Identity.Text(r.Name) == Identity.Text(other.Name)
                    && Identity.Text(r.Publisher).Length > 0 && Identity.Text(r.Publisher) == Identity.Text(other.Publisher))))
            return Deny("ambiguousNativeTarget", "存在另一个可能被 WinGet 命令匹配的注册表实例，拒绝执行。");
        return new(true, "ready", owners.Count == 1 ? "根据已确认的安装归属升级。" : "根据用户显式选择的管理器升级，历史安装来源仍为未知。", target);
    }

    public static bool SameInstallation(SoftwareInstance before, SoftwareInstance after) =>
        before.Records.OrderBy(r => r.Id).SequenceEqual(after.Records.OrderBy(r => r.Id), RecordComparer.Instance);

    private static UpgradeDecision Deny(string code, string reason) => new(false, code, reason);

    private sealed class RecordComparer : IEqualityComparer<SoftwareRecord>
    {
        public static RecordComparer Instance { get; } = new();
        public bool Equals(SoftwareRecord? a, SoftwareRecord? b) => a is not null && b is not null
            && Identity.Same(a.Id, b.Id) && a.Version == b.Version && a.Scope == b.Scope
            && a.Source == b.Source && Identity.Same(a.NativeId, b.NativeId)
            && a.Architecture == b.Architecture && a.RepositoryIdentifier == b.RepositoryIdentifier
            && a.Owner == b.Owner && a.UpgradeTarget == b.UpgradeTarget
            && a.ProductCodes.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.ProductCodes.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            && a.PackageFamilyNames.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(b.PackageFamilyNames.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)
            && StringComparer.OrdinalIgnoreCase.Equals(a.CanonicalDirectory ?? a.DirectoryPath,
                b.CanonicalDirectory ?? b.DirectoryPath);
        public int GetHashCode(SoftwareRecord obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id);
    }
}
