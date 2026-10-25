using Wpk.Core;

namespace Wpk.Tests;

public sealed class SearchAndUpgradeTests
{
    [Theory]
    [InlineData("Example App", 900)]
    [InlineData("Example", 700)]
    [InlineData("ample", 600)]
    [InlineData("exapp", 300)]
    [InlineData("winget:one", 1000)]
    public void SearchRanksDeterministically(string query, int score) =>
        Assert.Equal(score, Assert.Single(SearchService.Find([Fixtures.Instance(Fixtures.Winget())], query)).Score);

    [Fact]
    public void ExactSearchAndScopeDoNotSelectFuzzyCandidates()
    {
        var item = Fixtures.Instance(Fixtures.Winget() with { Aliases = ["tool"] });
        Assert.Empty(SearchService.Find([item], "Example", exact: true));
        Assert.Empty(SearchService.Find([item], "tool", scope: InstallScope.Machine));
        Assert.Single(SearchService.Find([item], "tool", exact: true));
        Assert.Single(SearchService.Find([item], "vendor.app", exact: true));
    }

    [Fact]
    public void EveryObservationIdCanSelectMergedInstance()
    {
        var item = Fixtures.Instance(Fixtures.Scoop(), Fixtures.Record(), Fixtures.Winget());
        Assert.Single(SearchService.Find([item], "registry:one", exact: true));
    }

    [Fact]
    public void WinGetRecognitionAloneNeverAuthorizesUpgrade()
    {
        var item = Fixtures.Instance(Fixtures.Winget());
        Assert.Equal("originUnknown", UpgradePlanner.Decide(item, null, [item]).Code);
        Assert.True(UpgradePlanner.Decide(item, PackageManagerKind.WinGet, [item]).Allowed);
        Assert.Empty(item.Owners);
    }

    [Fact]
    public void KnownScoopOwnershipCannotBeOverridden()
    {
        var item = Fixtures.Instance(Fixtures.Scoop(), Fixtures.Winget());
        Assert.Equal(PackageManagerKind.Scoop, UpgradePlanner.Decide(item, null, [item]).Target!.Manager);
        Assert.Equal("ownershipConflict", UpgradePlanner.Decide(item, PackageManagerKind.WinGet, [item]).Code);
    }

    [Fact]
    public void ConflictingOwnersAreAlwaysRejected()
    {
        var item = Fixtures.Instance(Fixtures.Scoop(), Fixtures.Winget() with { Owner = PackageManagerKind.WinGet });
        Assert.Equal("ownershipConflict", UpgradePlanner.Decide(item, PackageManagerKind.Scoop, [item]).Code);
    }

    [Fact]
    public void ExplicitManagerDoesNotBypassMultipleRepositories()
    {
        var a = Fixtures.Winget();
        var b = a with { Id = "winget:other", UpgradeTarget = a.UpgradeTarget! with { Repository = "private" } };
        var item = Fixtures.Instance(a, b);
        Assert.Equal("ambiguousUpgradeTarget", UpgradePlanner.Decide(item, PackageManagerKind.WinGet, [item]).Code);
    }

    [Fact]
    public void UnknownScopeAndRepositoryAreRejected()
    {
        var a = Fixtures.Winget();
        var item = Fixtures.Instance(a with { UpgradeTarget = a.UpgradeTarget! with { Scope = InstallScope.Unknown } });
        Assert.Equal("unknownScope", UpgradePlanner.Decide(item, PackageManagerKind.WinGet, [item]).Code);
        item = Fixtures.Instance(a with { UpgradeTarget = a.UpgradeTarget! with { Repository = null } });
        Assert.Equal("unknownRepository", UpgradePlanner.Decide(item, PackageManagerKind.WinGet, [item]).Code);
    }

    [Fact]
    public void RegistryOnlyAndUnrecognizedManagerAreRejected()
    {
        var item = Fixtures.Instance(Fixtures.Record());
        Assert.Equal("noUpgradeTarget", UpgradePlanner.Decide(item, PackageManagerKind.WinGet, [item]).Code);
        item = Fixtures.Instance(Fixtures.Winget());
        Assert.Equal("managerNotRecognized", UpgradePlanner.Decide(item, PackageManagerKind.Scoop, [item]).Code);
    }

    [Fact]
    public void DuplicateNativeTargetsCannotBeDisambiguatedByRecordId()
    {
        var a = Fixtures.Instance(Fixtures.Winget());
        var b = Fixtures.Instance(Fixtures.Winget("winget:two") with { DirectoryPath = @"D:\Other" });
        Assert.Equal("ambiguousNativeTarget", UpgradePlanner.Decide(a, PackageManagerKind.WinGet, [a, b]).Code);
    }

    [Fact]
    public void UncorrelatedRegistryDuplicateAlsoBlocksNativeUpgrade()
    {
        var a = Fixtures.Instance(Fixtures.Winget());
        var b = Fixtures.Instance(Fixtures.Record() with { DirectoryPath = @"D:\Other" });
        Assert.Equal("ambiguousNativeTarget", UpgradePlanner.Decide(a, PackageManagerKind.WinGet, [a, b]).Code);
    }
}
