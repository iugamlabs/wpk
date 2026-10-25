using System.Text.Json;
using Wpk.Cli.Platform;
using Wpk.Core;

namespace Wpk.Cli;

public sealed class CliApplication(IInventoryService inventory, IEnumerable<IUpgradeAdapter> adapters,
    IProcessRunner runner, IDirectoryOpener opener, TextWriter output, TextWriter error,
    Func<string, bool>? directoryExists = null,
    Func<InventorySnapshot, CancellationToken, Task<DoctorReport>>? doctor = null,
    bool interactive = true)
{
    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        CliOptions? options = null;
        try
        {
            try { options = CommandLine.Parse(args); }
            catch (ArgumentException e)
            { return Emit(args.Contains("--json"), ExitCodes.Usage, null, [new("cli", "invalidArguments", e.Message, DiagnosticSeverity.Error)], false); }
            if (options.Command is "help" or "version")
            {
                var text = options.Command == "help" ? CommandLine.Help : "wpk 0.1.0";
                return Emit(options.Json, 0, options.Command == "help" ? new { help = text } : (object)new { version = "0.1.0" }, [], false,
                    () => output.WriteLine(text));
            }
            var snapshot = await inventory.CollectAsync(cancellationToken);
            if (options.Command == "doctor")
            {
                var report = doctor is null ? new DoctorReport([], snapshot.Instances.Count) : await doctor(snapshot, cancellationToken);
                var exit = snapshot.Partial || report.Checks.Any(c => c.Status is "warning" or "error") ? ExitCodes.Unavailable : 0;
                return Emit(options.Json, exit, report, snapshot.Diagnostics, snapshot.Partial, () =>
                {
                    foreach (var c in report.Checks) output.WriteLine($"[{c.Status}] {c.Name}: {Output.Safe(c.Detail)}");
                    output.WriteLine($"安装实例：{report.InstalledInstances}");
                });
            }
            var matches = options.Command == "ls"
                ? snapshot.Instances.Where(i => options.Scope is null || i.Scope == options.Scope).ToArray()
                : SearchService.Find(snapshot.Instances, options.Query!, options.Exact, options.Scope).Select(m => m.Instance).ToArray();
            if (options.Command == "ls" || options.Command is "query" or "find")
                return Emit(options.Json, matches.Length == 0 && options.Command != "ls" ? ExitCodes.NotFound : 0,
                    matches.Select(InstanceOutput.From).ToArray(), snapshot.Diagnostics, snapshot.Partial, () =>
                    {
                        if (matches.Length == 0) output.WriteLine("未找到匹配的软件。");
                        else if (options.Command == "query" && matches.Length == 1) Output.Details(output, matches[0]);
                        else Output.Table(output, matches);
                    });
            if (matches.Length != 1)
                return Emit(options.Json, matches.Length == 0 ? ExitCodes.NotFound : ExitCodes.Ambiguous,
                    matches.Select(InstanceOutput.From).ToArray(), snapshot.Diagnostics.Append(new Diagnostic("cli",
                        matches.Length == 0 ? "notFound" : "ambiguousMatch", matches.Length == 0 ? "未找到匹配的软件。"
                            : "存在多个匹配，请复制实例 ID 或使用 --exact / --scope 缩小范围。", DiagnosticSeverity.Error)).ToArray(),
                    snapshot.Partial, () => { if (matches.Length > 0) Output.Table(output, matches); });
            var instance = matches[0];
            if (options.Command is "where" or "open") return Locate(options, instance, snapshot);
            return await UpgradeAsync(options, instance, snapshot, cancellationToken);
        }
        catch (OperationCanceledException)
        { return Emit(options?.Json ?? args.Contains("--json"), ExitCodes.Cancelled, null, [new("cli", "cancelled", "操作已取消；已启动的安装程序可能已产生变更。", DiagnosticSeverity.Error)], false); }
        catch (Exception e)
        { return Emit(options?.Json ?? args.Contains("--json"), ExitCodes.Unavailable, null, [new("cli", "operationFailed", e.Message, DiagnosticSeverity.Error)], false); }
    }

    private int Locate(CliOptions options, SoftwareInstance instance, InventorySnapshot snapshot)
    {
        var paths = instance.Records.Where(r => r.DirectoryPath is not null).ToArray();
        var canonical = paths.Select(r => Identity.PathKey(r.CanonicalDirectory ?? r.DirectoryPath))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (canonical.Length != 1)
            return Emit(options.Json, ExitCodes.Undetermined, InstanceOutput.From(instance),
                snapshot.Diagnostics.Append(new Diagnostic("cli", "directoryUnknown", "安装目录未知或存在冲突，无法定位。", DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);
        var directory = paths.FirstOrDefault(r => r.Source == SourceKind.Scoop)?.DirectoryPath ?? paths[0].DirectoryPath!;
        if (!(directoryExists ?? Directory.Exists)(directory!))
            return Emit(options.Json, ExitCodes.Unavailable, new { instanceId = instance.Id, directory },
                snapshot.Diagnostics.Append(new Diagnostic("cli", "directoryMissing", "记录的安装目录已不存在。", DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);
        if (options.Command == "open") opener.Open(directory!);
        return Emit(options.Json, 0, new { instanceId = instance.Id, directory, opened = options.Command == "open" },
            snapshot.Diagnostics, snapshot.Partial, () => output.WriteLine(options.Command == "where" ? directory : $"已打开：{directory}"));
    }

    private async Task<int> UpgradeAsync(CliOptions options, SoftwareInstance instance,
        InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        if (instance.Directories.Count > 0 && !instance.Directories.Any(directoryExists ?? Directory.Exists))
            return Denied(new(false, "installationDirectoryMissing", "记录的安装目录均已失效，请先确认安装状态。"), instance, snapshot, options.Json);
        var decision = Plan(instance, options.Via, snapshot);
        if (!decision.Allowed) return Denied(decision, instance, snapshot, options.Json);
        var adapter = adapters.SingleOrDefault(a => a.Manager == decision.Target!.Manager)
            ?? throw new InvalidOperationException("升级管理器适配器不可用。");
        var command = adapter.BuildCommand(decision.Target!, options.Json || !interactive);
        if (options.DryRun)
            return Emit(options.Json, 0, new { instanceId = instance.Id, decision, command, dryRun = true },
                snapshot.Diagnostics, snapshot.Partial, () => ShowCommand(decision, command));

        var fresh = await inventory.CollectAsync(cancellationToken);
        var reselected = fresh.Instances.SingleOrDefault(i => Identity.Same(i.Id, instance.Id));
        if (reselected is null || !UpgradePlanner.SameInstallation(instance, reselected))
            return Denied(new(false, "installationChanged", "安装记录已变化，请重新查询后再升级。"), instance, fresh, options.Json);
        var rechecked = Plan(reselected, options.Via, fresh);
        if (!rechecked.Allowed || rechecked.Target != decision.Target)
            return Denied(rechecked.Allowed ? new(false, "targetChanged", "升级目标已变化，请重新查询。") : rechecked, instance, fresh, options.Json);
        command = adapter.BuildCommand(rechecked.Target!, options.Json || !interactive);
        if (!options.Json) ShowCommand(rechecked, command);
        var result = await runner.RunAsync(command, null, !options.Json, cancellationToken);
        var exit = result.Cancelled ? ExitCodes.Cancelled : result.ExitCode == 0 && !result.TimedOut ? 0 : ExitCodes.ExternalFailure;
        var diagnostics = fresh.Diagnostics.ToList();
        if (exit != 0) diagnostics.Add(new("process", result.Cancelled ? "cancelled" : "externalFailure",
            $"管理器未成功完成；原始退出码：{result.ExitCode?.ToString() ?? "未知"}。", DiagnosticSeverity.Error));
        return Emit(options.Json, exit, new { instanceId = instance.Id, decision = rechecked, command, dryRun = false, process = result },
            diagnostics, fresh.Partial, () => output.WriteLine(exit == 0 ? "管理器执行完成。" : "请检查管理器输出。"));
    }

    private static UpgradeDecision Plan(SoftwareInstance instance, PackageManagerKind? via, InventorySnapshot snapshot)
    {
        var decision = UpgradePlanner.Decide(instance, via, snapshot.Instances);
        if (!decision.Allowed) return decision;
        var target = decision.Target!;
        var incomplete = snapshot.Diagnostics.Any(d => target.Manager == PackageManagerKind.Scoop ? d.Source == "scoop"
            : d.Source == "registry" || d.Source == "winget"
                && (d.Repository is null || Identity.Same(d.Repository, target.Repository)));
        if (snapshot.Partial && (snapshot.Diagnostics.Count == 0 || incomplete))
            return new(false, "incompleteInventory", "相关信息来源采集不完整，无法保证升级目标唯一；请先运行 doctor。");
        return decision;
    }

    private int Denied(UpgradeDecision decision, SoftwareInstance instance, InventorySnapshot snapshot, bool json) =>
        Emit(json, ExitCodes.Undetermined, new { instanceId = instance.Id, decision },
            snapshot.Diagnostics.Append(new Diagnostic("upgrade", decision.Code, decision.Reason, DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);

    private void ShowCommand(UpgradeDecision decision, CommandSpec command)
    {
        output.WriteLine(decision.Reason);
        output.WriteLine($"执行：{JsonSerializer.Serialize(command.FileName)} {string.Join(' ', command.Arguments.Select(a => JsonSerializer.Serialize(a)))}");
    }

    private int Emit(bool json, int exit, object? data, IReadOnlyList<Diagnostic> diagnostics, bool partial, Action? human = null)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(new ResponseEnvelope(1, data, diagnostics, partial, exit), Output.JsonOptions));
        else
        {
            human?.Invoke();
            foreach (var diagnostic in diagnostics)
                error.WriteLine($"[{diagnostic.Source}/{diagnostic.Code}] {Output.Safe(diagnostic.Message)}");
        }
        return exit;
    }
}
