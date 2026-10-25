using Wpk.Core;

namespace Wpk.Tests;

internal static class Fixtures
{
    public static SoftwareRecord Record(string id = "registry:one", SourceKind source = SourceKind.Registry) => new()
    {
        Id = id, Source = source, NativeId = source == SourceKind.WinGet ? "Vendor.App" : "app",
        Name = "Example App", Publisher = "Vendor", Version = "1.0", Scope = InstallScope.User,
        DirectoryPath = @"C:\Apps\Example", CanonicalDirectory = @"C:\Apps\Example"
    };

    public static SoftwareRecord Winget(string id = "winget:one") => Record(id, SourceKind.WinGet) with
    {
        UpgradeTarget = new(PackageManagerKind.WinGet, "Vendor.App", InstallScope.User, "winget"),
        RepositoryIdentifier = "source-id"
    };

    public static SoftwareRecord Scoop(string id = "scoop:one") => Record(id, SourceKind.Scoop) with
    {
        Owner = PackageManagerKind.Scoop,
        UpgradeTarget = new(PackageManagerKind.Scoop, "app", InstallScope.User, "main", @"C:\scoop")
    };

    public static SoftwareInstance Instance(params SoftwareRecord[] records) => new(records[0].Id, records, []);
    public static InventorySnapshot Snapshot(params SoftwareInstance[] instances) => new(instances, [], false);
}

internal sealed class TempDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "wpk-tests-" + Guid.NewGuid().ToString("N"));
    public TempDirectory() => Directory.CreateDirectory(Root);
    public string Create(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }
    public void Dispose()
    {
        var absolute = Path.GetFullPath(Root);
        if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "wpk-tests-", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected test cleanup path.");
        if (Directory.Exists(absolute)) Directory.Delete(absolute, true);
    }
}

internal sealed class FakeInventory(params InventorySnapshot[] snapshots) : IInventoryService
{
    public int Calls { get; private set; }
    public Task<InventorySnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(snapshots[Math.Min(Calls++, snapshots.Length - 1)]);
    }
}

internal sealed class FakeRunner : IProcessRunner
{
    public int Calls { get; private set; }
    public bool StreamOutput { get; private set; }
    public ProcessResult Result { get; set; } = new(0, "manager stdout", "manager stderr");
    public Task<ProcessResult> RunAsync(CommandSpec command, TimeSpan? timeout, bool streamOutput, CancellationToken cancellationToken)
    { Calls++; StreamOutput = streamOutput; return Task.FromResult(Result); }
}

internal sealed class FakeOpener : IDirectoryOpener
{
    public string? Opened { get; private set; }
    public void Open(string directory) => Opened = directory;
}

internal sealed class FakeAdapter(PackageManagerKind manager) : IUpgradeAdapter
{
    public PackageManagerKind Manager => manager;
    public bool NonInteractive { get; private set; }
    public CommandSpec BuildCommand(UpgradeTarget target, bool nonInteractive)
    { NonInteractive = nonInteractive; return new("fake-manager.exe", ["upgrade", target.PackageId]); }
}
