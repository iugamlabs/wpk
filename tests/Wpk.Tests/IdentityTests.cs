using Wpk.Core;

namespace Wpk.Tests;

public sealed class IdentityTests
{
    private readonly InstanceMerger merger = new();

    [Fact]
    public void ThreeStrongObservationsMergeAndKeepAllEvidence()
    {
        var records = new[] { Fixtures.Scoop(), Fixtures.Winget(), Fixtures.Record() }
            .Select(r => r with { ProductCodes = ["{PRODUCT}"] }).ToArray();
        var instance = Assert.Single(merger.Merge(records));
        Assert.Equal(3, instance.Records.Count);
        Assert.Contains("productCode", instance.MergeReasons);
        Assert.Equal([PackageManagerKind.Scoop], instance.Owners);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("scope")]
    [InlineData("version")]
    [InlineData("publisher")]
    [InlineData("architecture")]
    [InlineData("productCode")]
    [InlineData("family")]
    public void ConflictingInstallationsStaySeparate(string conflict)
    {
        var a = Fixtures.Winget() with { Architecture = "64bit", ProductCodes = ["P1"], PackageFamilyNames = ["F1"] };
        var b = Fixtures.Record() with { Architecture = "64bit", ProductCodes = ["P1"], PackageFamilyNames = ["F1"] };
        b = conflict switch
        {
            "path" => b with { DirectoryPath = @"C:\Other", CanonicalDirectory = @"C:\Other" },
            "scope" => b with { Scope = InstallScope.Machine },
            "version" => b with { Version = "2.0" },
            "publisher" => b with { Publisher = "Other Vendor" },
            "architecture" => b with { Architecture = "32bit" },
            "productCode" => b with { ProductCodes = ["P2"] },
            _ => b with { PackageFamilyNames = ["F2"] }
        };
        Assert.Equal(2, merger.Merge([a, b]).Count);
    }

    [Fact]
    public void WeakIdentityRequiresMutualUniqueness()
    {
        var a = Fixtures.Winget() with { DirectoryPath = null, CanonicalDirectory = null };
        var b = Fixtures.Record("registry:a") with { DirectoryPath = null, CanonicalDirectory = null };
        var c = b with { Id = "registry:b" };
        Assert.Equal(3, merger.Merge([a, b, c]).Count);
        Assert.Single(merger.Merge([a, b]));
    }

    [Fact]
    public void UnknownPathCannotBridgeTwoDifferentDirectories()
    {
        var a = Fixtures.Winget() with { DirectoryPath = null, CanonicalDirectory = null, ProductCodes = ["P"] };
        var b = Fixtures.Record("registry:a") with { ProductCodes = ["P"] };
        var c = b with { Id = "registry:b", DirectoryPath = @"D:\App", CanonicalDirectory = @"D:\App" };
        Assert.Equal(3, merger.Merge([a, b, c]).Count);
    }

    [Fact]
    public void FuzzyNamesAreNeverAnIdentity()
    {
        var a = Fixtures.Winget() with { DirectoryPath = null, CanonicalDirectory = null, Publisher = null };
        var b = Fixtures.Record() with { Name = "Example Application", DirectoryPath = null, CanonicalDirectory = null, Publisher = null };
        Assert.Equal(2, merger.Merge([a, b]).Count);
    }

    [Fact]
    public void RegistryViewsCanDescribeTheSameInstance()
    {
        var a = Fixtures.Record("registry:32") with { RegistryView = "Registry32", ProductCodes = ["P"] };
        var b = a with { Id = "registry:64", RegistryView = "Registry64" };
        Assert.Single(merger.Merge([a, b]));
    }

    [Fact]
    public void PortableOwnershipNeedsExactCrossValidation()
    {
        var a = Fixtures.Winget();
        var marker = Fixtures.Record() with { PortableMarker = new("Vendor.App", "source-id") };
        Assert.Equal([PackageManagerKind.WinGet], Assert.Single(merger.Merge([a, marker])).Owners);
        marker = marker with { PortableMarker = new("Vendor.App", "other-source") };
        Assert.Empty(Assert.Single(merger.Merge([a, marker])).Owners);
    }

    [Fact]
    public void RecordIdsAreStableAndDelimited()
    {
        Assert.Equal(Identity.RecordId("scoop", "ROOT", "APP"), Identity.RecordId("scoop", "root", "app"));
        Assert.NotEqual(Identity.RecordId("scoop", "ab", "c"), Identity.RecordId("scoop", "a", "bc"));
    }

    [Fact]
    public void MergeIsIndependentOfInputOrder()
    {
        var values = new[] { Fixtures.Winget(), Fixtures.Record(), Fixtures.Scoop() };
        Assert.Equal(merger.Merge(values).Select(i => i.Id), merger.Merge(values.Reverse()).Select(i => i.Id));
    }
}
