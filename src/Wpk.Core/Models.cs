namespace Wpk.Core;

public enum SourceKind { Scoop, WinGet, Registry }
public enum PackageManagerKind { Scoop, WinGet }
public enum InstallScope { Unknown, User, Machine }
public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record Diagnostic(string Source, string Code, string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Warning, string? Repository = null);
public sealed record Evidence(string Kind, string Location, string Description);
public sealed record PortableOwnershipMarker(string PackageId, string SourceIdentifier);

public sealed record UpgradeTarget(
    PackageManagerKind Manager, string PackageId, InstallScope Scope,
    string? Repository = null, string? Root = null);

/// <summary>An observation of an installation, not an assertion of its historical installer.</summary>
public sealed record SoftwareRecord
{
    public required string Id { get; init; }
    public required SourceKind Source { get; init; }
    public required string NativeId { get; init; }
    public required string Name { get; init; }
    public string? Version { get; init; }
    public string? Publisher { get; init; }
    public InstallScope Scope { get; init; }
    public string? Architecture { get; init; }
    public string? RegistryView { get; init; }
    public string? RepositoryIdentifier { get; init; }
    public PortableOwnershipMarker? PortableMarker { get; init; }
    public string? DirectoryPath { get; init; }
    public string? CanonicalDirectory { get; init; }
    public IReadOnlyList<string> ProductCodes { get; init; } = [];
    public IReadOnlyList<string> PackageFamilyNames { get; init; } = [];
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public PackageManagerKind? Owner { get; init; }
    public UpgradeTarget? UpgradeTarget { get; init; }
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}

public sealed record SoftwareInstance(string Id, IReadOnlyList<SoftwareRecord> Records,
    IReadOnlyList<string> MergeReasons)
{
    public string Name => Primary.Name;
    public string? Version => Primary.Version ?? Records.Select(r => r.Version).FirstOrDefault(v => v is not null);
    public InstallScope Scope => Records.Select(r => r.Scope).FirstOrDefault(s => s != InstallScope.Unknown);
    public SoftwareRecord Primary => Records.OrderBy(r => r.Source switch
    {
        SourceKind.Scoop => 0, SourceKind.Registry => 1, _ => 2
    }).ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase).First();
    public IReadOnlyList<string> Directories => Records.Select(r => r.DirectoryPath)
        .OfType<string>().Where(p => !string.IsNullOrWhiteSpace(p))
        .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
    public IReadOnlyList<PackageManagerKind> Owners => Records.Where(r => r.Owner.HasValue)
        .Select(r => r.Owner!.Value).Distinct().ToArray();
}

public sealed record SourceResult(IReadOnlyList<SoftwareRecord> Records,
    IReadOnlyList<Diagnostic> Diagnostics, bool Partial = false);
public sealed record InventorySnapshot(IReadOnlyList<SoftwareInstance> Instances,
    IReadOnlyList<Diagnostic> Diagnostics, bool Partial);
public sealed record UpgradeDecision(bool Allowed, string Code, string Reason, UpgradeTarget? Target = null);
public sealed record CommandSpec(string FileName, IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null);
public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError,
    bool TimedOut = false, bool Cancelled = false);

public interface IInventorySource
{
    string Name { get; }
    Task<SourceResult> CollectAsync(CancellationToken cancellationToken);
}

public interface IUpgradeAdapter
{
    PackageManagerKind Manager { get; }
    CommandSpec BuildCommand(UpgradeTarget target, bool nonInteractive);
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(CommandSpec command, TimeSpan? timeout,
        bool streamOutput, CancellationToken cancellationToken);
}

public interface IDirectoryOpener
{
    void Open(string directory);
}

public interface IInventoryService
{
    Task<InventorySnapshot> CollectAsync(CancellationToken cancellationToken);
}
