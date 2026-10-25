using System.Text.Json;
using System.Text.Json.Serialization;
using Wpk.Core;

namespace Wpk.Cli;

public static class ExitCodes
{
    public const int Success = 0, NotFound = 1, Usage = 2, Ambiguous = 3,
        Undetermined = 4, Unavailable = 5, ExternalFailure = 6, Cancelled = 130;
}

public sealed record ResponseEnvelope(int SchemaVersion, object? Data,
    IReadOnlyList<Diagnostic> Diagnostics, bool Partial, int ExitCode);

public sealed record InstanceOutput(string Id, string Name, string? Version, InstallScope Scope,
    IReadOnlyList<PackageManagerKind> Owners, IReadOnlyList<string> Directories,
    IReadOnlyList<SoftwareRecord> Records, IReadOnlyList<string> MergeReasons)
{
    public static InstanceOutput From(SoftwareInstance i) => new(i.Id, i.Name, i.Version, i.Scope,
        i.Owners, i.Directories, i.Records, i.MergeReasons);
}

public static class Output
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(new LowerCasePolicy()) }
    };

    public static void Table(TextWriter writer, IEnumerable<SoftwareInstance> instances)
    {
        writer.WriteLine("实例 ID                     软件 / 版本 / 范围 / 安装归属 / 观察来源");
        foreach (var i in instances)
            writer.WriteLine($"{i.Id,-29} {Safe(i.Name)} / {Safe(i.Version ?? "未知")} / {Scope(i.Scope)} / {Owner(i)} / {string.Join(',', i.Records.Select(r => r.Source.ToString().ToLowerInvariant()).Distinct())}");
    }

    public static void Details(TextWriter writer, SoftwareInstance instance)
    {
        writer.WriteLine($"{Safe(instance.Name)}  {Safe(instance.Version ?? "版本未知")}");
        writer.WriteLine($"实例 ID：{instance.Id}");
        writer.WriteLine($"范围：{Scope(instance.Scope)}；安装归属：{Owner(instance)}");
        writer.WriteLine($"目录：{string.Join("；", instance.Directories.Select(Safe).DefaultIfEmpty("未知"))}");
        foreach (var record in instance.Records)
        {
            writer.WriteLine($"  [{record.Source.ToString().ToLowerInvariant()}] {Safe(record.NativeId)}");
            foreach (var evidence in record.Evidence)
                writer.WriteLine($"    {Safe(evidence.Description)} ({Safe(evidence.Location)})");
        }
    }

    public static string Safe(string value) => string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c));
    public static string Scope(InstallScope scope) => scope switch
    { InstallScope.User => "user", InstallScope.Machine => "machine", _ => "unknown" };
    private static string Owner(SoftwareInstance i) => i.Owners.Count == 0 ? "未知"
        : string.Join(',', i.Owners.Select(o => o.ToString().ToLowerInvariant()));
    private sealed class LowerCasePolicy : JsonNamingPolicy
    { public override string ConvertName(string name) => name.ToLowerInvariant(); }
}
