using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Wpk.Cli.Platform;
using Wpk.Core;

namespace Wpk.Cli;

public static class ExitCodes
{
    public const int Success = 0, NotFound = 1, Usage = 2, Ambiguous = 3,
        Undetermined = 4, Unavailable = 5, ExternalFailure = 6, Cancelled = 130;
}

public sealed record ResponseEnvelope(int SchemaVersion, JsonNode? Data,
    IReadOnlyList<Diagnostic> Diagnostics, bool Partial, int ExitCode);

public sealed record InstanceOutput(string Id, string Name, string? Version, InstallScope Scope,
    IReadOnlyList<PackageManagerKind> Owners, IReadOnlyList<string> Directories,
    IReadOnlyList<SoftwareRecord> Records, IReadOnlyList<string> MergeReasons)
{
    public static InstanceOutput From(SoftwareInstance i) => new(i.Id, i.Name, i.Version, i.Scope,
        i.Owners, i.Directories, i.Records, i.MergeReasons);
}

public sealed record VersionPayload(string Version);
public sealed record HelpPayload(string Help);
public sealed record DirectoryPayload(
    string InstanceId,
    string Directory,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Opened = null);
public sealed record DeniedPayload(string InstanceId, UpgradeDecision Decision);
public sealed record UpgradePayload(
    string InstanceId,
    UpgradeDecision Decision,
    CommandSpec Command,
    bool DryRun,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? ConfirmationRequired = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProcessResult? Process = null);

public static class Output
{
    public static string Safe(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));
    public static string Scope(InstallScope scope) => scope switch
    { InstallScope.User => "user", InstallScope.Machine => "machine", _ => "unknown" };
    public static string Owner(SoftwareInstance i) => i.Owners.Count == 0 ? "未知"
        : string.Join(',', i.Owners.Select(o => o.ToString().ToLowerInvariant()));

    public static string SerializeEnvelope(int exit, object? data, IReadOnlyList<Diagnostic> diagnostics, bool partial)
    {
        var node = data switch
        {
            null => null,
            JsonNode json => json,
            InstanceOutput[] instances => JsonSerializer.SerializeToNode(instances, WpkJsonContext.Default.InstanceOutputArray),
            InstanceOutput instance => JsonSerializer.SerializeToNode(instance, WpkJsonContext.Default.InstanceOutput),
            DoctorReport report => JsonSerializer.SerializeToNode(report, WpkJsonContext.Default.DoctorReport),
            VersionPayload version => JsonSerializer.SerializeToNode(version, WpkJsonContext.Default.VersionPayload),
            HelpPayload help => JsonSerializer.SerializeToNode(help, WpkJsonContext.Default.HelpPayload),
            DirectoryPayload directory => JsonSerializer.SerializeToNode(directory, WpkJsonContext.Default.DirectoryPayload),
            DeniedPayload denied => JsonSerializer.SerializeToNode(denied, WpkJsonContext.Default.DeniedPayload),
            UpgradePayload upgrade => JsonSerializer.SerializeToNode(upgrade, WpkJsonContext.Default.UpgradePayload),
            _ => throw new InvalidOperationException($"Unsupported JSON payload type: {data.GetType()}")
        };
        return JsonSerializer.Serialize(
            new ResponseEnvelope(1, node, diagnostics, partial, exit),
            WpkJsonContext.Default.ResponseEnvelope);
    }
}

/// <summary>Keeps enum JSON values lowercase (user/scoop/winget) to match the existing --json contract.</summary>
internal static class LowerCaseJsonEnums
{
    public static readonly JsonNamingPolicy Policy = new LowerCasePolicy();

    private sealed class LowerCasePolicy : JsonNamingPolicy
    {
        public override string ConvertName(string name) => name.ToLowerInvariant();
    }
}

internal sealed class LowerCaseInstallScopeConverter()
    : JsonStringEnumConverter<InstallScope>(LowerCaseJsonEnums.Policy);
internal sealed class LowerCasePackageManagerKindConverter()
    : JsonStringEnumConverter<PackageManagerKind>(LowerCaseJsonEnums.Policy);
internal sealed class LowerCaseSourceKindConverter()
    : JsonStringEnumConverter<SourceKind>(LowerCaseJsonEnums.Policy);
internal sealed class LowerCaseDiagnosticSeverityConverter()
    : JsonStringEnumConverter<DiagnosticSeverity>(LowerCaseJsonEnums.Policy);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    Converters =
    [
        typeof(LowerCaseInstallScopeConverter),
        typeof(LowerCasePackageManagerKindConverter),
        typeof(LowerCaseSourceKindConverter),
        typeof(LowerCaseDiagnosticSeverityConverter)
    ])]
[JsonSerializable(typeof(ResponseEnvelope))]
[JsonSerializable(typeof(InstanceOutput))]
[JsonSerializable(typeof(InstanceOutput[]))]
[JsonSerializable(typeof(DoctorReport))]
[JsonSerializable(typeof(DoctorCheck))]
[JsonSerializable(typeof(VersionPayload))]
[JsonSerializable(typeof(HelpPayload))]
[JsonSerializable(typeof(DirectoryPayload))]
[JsonSerializable(typeof(DeniedPayload))]
[JsonSerializable(typeof(UpgradePayload))]
[JsonSerializable(typeof(Diagnostic))]
[JsonSerializable(typeof(IReadOnlyList<Diagnostic>))]
[JsonSerializable(typeof(SoftwareRecord))]
[JsonSerializable(typeof(UpgradeDecision))]
[JsonSerializable(typeof(UpgradeTarget))]
[JsonSerializable(typeof(CommandSpec))]
[JsonSerializable(typeof(ProcessResult))]
[JsonSerializable(typeof(Evidence))]
[JsonSerializable(typeof(PortableOwnershipMarker))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(IReadOnlyList<PackageManagerKind>))]
[JsonSerializable(typeof(IReadOnlyList<SoftwareRecord>))]
[JsonSerializable(typeof(IReadOnlyList<Evidence>))]
[JsonSerializable(typeof(IReadOnlyList<DoctorCheck>))]
internal partial class WpkJsonContext : JsonSerializerContext;
