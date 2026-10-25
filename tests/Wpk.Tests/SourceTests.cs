using System.Text.Json;
using Wpk.Cli.Platform;
using Wpk.Core;

namespace Wpk.Tests;

public sealed class SourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScoopReadsOldAndNewMetadataAndIgnoresHistory(bool modern)
    {
        using var temp = new TempDirectory();
        var current = temp.Create("user/apps/tool/current");
        var historical = temp.Create("user/apps/tool/0.9");
        WriteScoop(current, modern);
        WriteScoop(historical, modern);
        var result = await new ScoopSource(new(Path.Combine(temp.Root, "user"), Path.Combine(temp.Root, "global")))
            .CollectAsync(default);
        var record = Assert.Single(result.Records);
        Assert.False(result.Partial);
        Assert.Equal("1.0", record.Version);
        Assert.Equal(InstallScope.User, record.Scope);
        Assert.Equal(PackageManagerKind.Scoop, record.Owner);
        Assert.Contains("alias", record.Aliases);
        Assert.Contains("more", record.Aliases);
        Assert.Equal("main", record.UpgradeTarget!.Repository);
    }

    [Fact]
    public async Task ScoopKeepsUserAndGlobalInstallationsSeparate()
    {
        using var temp = new TempDirectory();
        WriteScoop(temp.Create("user/apps/tool/current"), true);
        WriteScoop(temp.Create("global/apps/tool/current"), true);
        var result = await new ScoopSource(new(Path.Combine(temp.Root, "user"), Path.Combine(temp.Root, "global"))).CollectAsync(default);
        Assert.Equal(2, result.Records.Count);
        Assert.Contains(result.Records, r => r.Scope == InstallScope.Machine);
        Assert.Equal(2, new InstanceMerger().Merge(result.Records).Count);
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("install")]
    [InlineData("current")]
    public async Task DamagedScoopEntryDoesNotHideHealthyEntries(string damage)
    {
        using var temp = new TempDirectory();
        WriteScoop(temp.Create("apps/good/current"), true);
        var bad = temp.Create(damage == "current" ? "apps/bad/1.0" : "apps/bad/current");
        WriteScoop(bad, true);
        if (damage == "manifest") File.WriteAllText(Path.Combine(bad, "scoop-manifest.json"), "{");
        if (damage == "install") File.WriteAllText(Path.Combine(bad, "scoop-install.json"), "{}");
        var result = await new ScoopSource(new(temp.Root, Path.Combine(temp.Root, "global"))).CollectAsync(default);
        Assert.True(result.Partial);
        Assert.Equal("good", Assert.Single(result.Records).NativeId);
        Assert.Contains(result.Diagnostics, d => d.Code == "invalidInstallation");
    }

    [Fact]
    public async Task NewScoopMetadataTakesPrecedenceWithoutUnsafeFallback()
    {
        using var temp = new TempDirectory();
        var path = temp.Create("apps/tool/current");
        WriteScoop(path, false);
        File.WriteAllText(Path.Combine(path, "scoop-install.json"), "{");
        var result = await new ScoopSource(new(temp.Root, Path.Combine(temp.Root, "global"))).CollectAsync(default);
        Assert.Empty(result.Records);
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task NoJunctionOnlyAcceptsOneUnambiguousVersion()
    {
        using var temp = new TempDirectory();
        WriteScoop(temp.Create("apps/tool/1.0"), true);
        var source = new ScoopSource(new(temp.Root, Path.Combine(temp.Root, "global"), true));
        Assert.Single((await source.CollectAsync(default)).Records);
        WriteScoop(temp.Create("apps/tool/0.9"), true);
        var ambiguous = await source.CollectAsync(default);
        Assert.Empty(ambiguous.Records);
        Assert.True(ambiguous.Partial);
    }

    [Fact]
    public void RegistryDoesNotInventOfficialOrManagerOwnership()
    {
        var record = RegistrySource.Map(Entry(new Dictionary<string, object?>
        {
            ["DisplayName"] = "Example", ["DisplayVersion"] = "1", ["Publisher"] = "Vendor",
            ["UninstallString"] = @"C:\Windows\System32\msiexec.exe /x {123}",
            ["DisplayIcon"] = @"C:\Windows\System32\msiexec.exe,0"
        }))!;
        Assert.Null(record.Owner);
        Assert.Null(record.UpgradeTarget);
        Assert.Null(record.DirectoryPath);
        Assert.Null(record.Architecture);
        Assert.Equal("Registry32", record.RegistryView);
        Assert.Equal("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", Assert.Single(record.ProductCodes));
    }

    [Fact]
    public void RegistryMarkerIsOnlyEvidenceUntilCrossValidated()
    {
        var record = RegistrySource.Map(Entry(new Dictionary<string, object?>
        {
            ["DisplayName"] = "Example", ["InstallLocation"] = @"C:\Apps\Example",
            ["WinGetPackageIdentifier"] = "Vendor.App", ["WinGetSourceIdentifier"] = "source-id",
            ["UninstallString"] = "winget uninstall --id Vendor.App"
        }))!;
        Assert.NotNull(record.PortableMarker);
        Assert.Null(record.Owner);
    }

    [Fact]
    public void RegistryFiltersSystemComponentsAndNamelessRecords()
    {
        Assert.Null(RegistrySource.Map(Entry(new Dictionary<string, object?>())));
        Assert.Null(RegistrySource.Map(Entry(new Dictionary<string, object?> { ["DisplayName"] = "Hidden", ["SystemComponent"] = 1 })));
    }

    [Fact]
    public void WinGetMappingIsRecognitionOnlyAndPreservesRepositoryAndIdentity()
    {
        var record = WinGetSource.Map(new("Vendor.App", "Example", "1.0", "Vendor", InstallScope.User,
            "custom repository", "source-id", null, ["P"], ["F"], "installed-id"));
        Assert.Null(record.Owner);
        Assert.Null(record.DirectoryPath);
        Assert.Equal("custom repository", record.UpgradeTarget!.Repository);
        Assert.Equal("Vendor.App", record.NativeId);
        Assert.Contains(record.Evidence, e => e.Kind == "catalogRecognition");
    }

    [Fact]
    public async Task BrokenSourceStillReturnsOtherInventoryWithDiagnostics()
    {
        var service = new InventoryService([new StubSource(false), new StubSource(true)]);
        var result = await service.CollectAsync(default);
        Assert.Single(result.Instances);
        Assert.True(result.Partial);
        Assert.Equal("sourceFailed", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public async Task InventoryCancellationIsNotConvertedIntoPartialSuccess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ScoopSource(new("C:\\missing", "D:\\missing")).CollectAsync(cancellation.Token));
    }

    private static UninstallEntry Entry(IReadOnlyDictionary<string, object?> values) =>
        new("{AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE}", InstallScope.User, "Registry32", values);

    private static void WriteScoop(string directory, bool modern)
    {
        File.WriteAllText(Path.Combine(directory, modern ? "scoop-manifest.json" : "manifest.json"),
            """{"version":"1.0","bin":[["tool.exe","alias"],"more.exe"]}""");
        File.WriteAllText(Path.Combine(directory, modern ? "scoop-install.json" : "install.json"),
            JsonSerializer.Serialize(new { bucket = "main", architecture = "64bit" }));
    }

    private sealed class StubSource(bool broken) : IInventorySource
    {
        public string Name => broken ? "broken" : "healthy";
        public Task<SourceResult> CollectAsync(CancellationToken cancellationToken) => broken
            ? throw new IOException("test source denied") : Task.FromResult(new SourceResult([Fixtures.Record()], []));
    }
}
