using System.Text.Json;
using Spectre.Console.Cli;
using Wpk.Cli.Platform;
using Wpk.Cli.Presentation;
using Wpk.Core;

namespace Wpk.Cli;

public sealed class CliApplication
{
    private readonly IInventoryService inventory;
    private readonly IUpgradeAdapter[] adapters;
    private readonly IProcessRunner runner;
    private readonly IDirectoryOpener opener;
    private readonly TextWriter output;
    private readonly Func<string, bool> directoryExists;
    private readonly Func<InventorySnapshot, CancellationToken, Task<DoctorReport>>? doctor;
    private readonly bool interactive;
    private readonly ICliRenderer renderer;
    private readonly IUserInteraction? interaction;

    public CliApplication(IInventoryService inventory, IEnumerable<IUpgradeAdapter> adapters,
        IProcessRunner runner, IDirectoryOpener opener, TextWriter output, TextWriter error,
        Func<string, bool>? directoryExists = null,
        Func<InventorySnapshot, CancellationToken, Task<DoctorReport>>? doctor = null,
        bool interactive = true, ICliRenderer? renderer = null, IUserInteraction? userInteraction = null)
    {
        this.inventory = inventory; this.adapters = adapters.ToArray(); this.runner = runner; this.opener = opener;
        this.output = output; this.directoryExists = directoryExists ?? Directory.Exists;
        this.doctor = doctor; this.interactive = interactive; interaction = userInteraction;
        var terminal = new TerminalContext(output, error);
        this.renderer = renderer ?? new SpectreRenderer(terminal.Output, terminal.Error);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var options = new CliOptions("query", null, CommandLine.HasFlag(args, "--json"), false, null, null, false,
            CommandLine.HasFlag(args, "--verbose"));
        try
        {
            var result = await CommandLine.InvokeAsync(args, (parsed, token) =>
            { options = parsed; return ExecuteAsync(parsed, token); }, cancellationToken);
            if (result.Executed) return result.ExitCode;
            if (result.ExitCode != 0)
                return Emit(options, ExitCodes.Usage, null,
                    [new("cli", "invalidArguments", result.Text, DiagnosticSeverity.Error)], false);
            return Emit(options, 0, result.Version ? new { version = CommandLine.Version } : (object)new { help = result.Text },
                [], false, () => output.Write(result.Text));
        }
        catch (OperationCanceledException)
        { return Emit(options, ExitCodes.Cancelled, null, [new("cli", "cancelled", "操作已取消；未启动的升级不会执行，已启动的安装程序可能已产生变更。", DiagnosticSeverity.Error)], false); }
        catch (Exception e) when (e is CommandAppException or ArgumentException)
        { return Emit(options, ExitCodes.Usage, null, [new("cli", "invalidArguments", e.Message, DiagnosticSeverity.Error)], false); }
        catch (Exception e)
        { return Emit(options, ExitCodes.Unavailable, null, [new("cli", "operationFailed", e.Message, DiagnosticSeverity.Error)], false); }
    }

    private bool CanInteract(CliOptions options) => !options.Json && interactive && interaction?.IsInteractive == true;

    private Task<InventorySnapshot> CollectAsync(CliOptions options, string status, CancellationToken cancellationToken) =>
        renderer.StatusAsync(status, () => inventory.CollectAsync(cancellationToken), !options.Json);

    private async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken)
    {
        var snapshot = await CollectAsync(options, "正在查询本机安装记录…", cancellationToken);
        if (options.Command == "doctor")
        {
            var report = doctor is null ? new DoctorReport([], snapshot.Instances.Count) : await doctor(snapshot, cancellationToken);
            var exit = snapshot.Partial || report.Checks.Any(c => c.Status is "warning" or "error") ? ExitCodes.Unavailable : 0;
            return Emit(options, exit, report, snapshot.Diagnostics, snapshot.Partial, () => renderer.Doctor(report));
        }
        var matches = options.Command == "ls" ? snapshot.Instances.Where(i => options.Scope is null || i.Scope == options.Scope)
            .Select(i => new SearchMatch(i, 0, MatchKind.None)).ToArray()
            : SearchService.Find(snapshot.Instances, options.Query!, options.Exact, options.Scope).ToArray();
        if (options.Command is "ls" or "query" or "find")
        {
            var instances = matches.Select(m => m.Instance).ToArray();
            return Emit(options, instances.Length == 0 && options.Command != "ls" ? ExitCodes.NotFound : 0,
                instances.Select(InstanceOutput.From).ToArray(), snapshot.Diagnostics, snapshot.Partial, () =>
                {
                    if (instances.Length == 0) renderer.Message("未找到匹配的软件。");
                    else if (options.Command == "query" && instances.Length == 1) renderer.Details(instances[0], options.Verbose);
                    else renderer.List(instances, options.Verbose);
                });
        }

        // Stable observation IDs outrank names; exact names outrank partial matches, never vice versa.
        var precise = matches.Where(m => m.Kind == MatchKind.RecordId).ToArray();
        if (precise.Length == 0) precise = matches.Where(m => m.IsExact).ToArray();
        var candidates = precise.Length > 0 ? precise : matches;
        var selectedByUser = false;
        SearchMatch? selected = candidates.Length == 1 ? candidates[0] : null;
        if (candidates.Length > 1 && CanInteract(options) && !options.DryRun)
        {
            var choice = await interaction!.SelectAsync(candidates.Select(m => m.Instance).ToArray(), cancellationToken);
            if (choice is null) return Cancelled(options, snapshot);
            selected = candidates.SingleOrDefault(m => m.Instance.Id == choice.Id)
                ?? throw new OperationCanceledException(cancellationToken);
            selectedByUser = true;
        }
        if (selected is null)
        {
            var values = candidates.Select(m => m.Instance).ToArray();
            return Emit(options, values.Length == 0 ? ExitCodes.NotFound : ExitCodes.Ambiguous,
                values.Select(InstanceOutput.From).ToArray(), snapshot.Diagnostics.Append(new Diagnostic("cli",
                    values.Length == 0 ? "notFound" : "ambiguousMatch", values.Length == 0 ? "未找到匹配的软件。"
                        : "存在多个匹配，请使用完整实例 ID，或通过 --exact / --scope 缩小范围。", DiagnosticSeverity.Error)).ToArray(),
                snapshot.Partial, () => { if (values.Length > 0) renderer.List(values, showIds: true); });
        }
        if (options.Command is "where" or "open") return Locate(options, selected.Instance, snapshot);
        return await UpgradeAsync(options, selected.Instance, selected.IsExact || selectedByUser, snapshot, cancellationToken);
    }

    private int Locate(CliOptions options, SoftwareInstance instance, InventorySnapshot snapshot)
    {
        var paths = instance.Records.Where(r => r.DirectoryPath is not null).ToArray();
        var canonical = paths.Select(r => Identity.PathKey(r.CanonicalDirectory ?? r.DirectoryPath))
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (canonical.Length != 1)
            return Emit(options, ExitCodes.Undetermined, InstanceOutput.From(instance),
                snapshot.Diagnostics.Append(new Diagnostic("cli", "directoryUnknown", "安装目录未知或存在冲突，无法定位。", DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);
        var directory = paths.FirstOrDefault(r => r.Source == SourceKind.Scoop)?.DirectoryPath ?? paths[0].DirectoryPath!;
        if (!directoryExists(directory!))
            return Emit(options, ExitCodes.Unavailable, new { instanceId = instance.Id, directory },
                snapshot.Diagnostics.Append(new Diagnostic("cli", "directoryMissing", "记录的安装目录已不存在。", DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);
        if (options.Command == "open") opener.Open(directory!);
        return Emit(options, 0, new { instanceId = instance.Id, directory, opened = options.Command == "open" },
            snapshot.Diagnostics, snapshot.Partial, () =>
            {
                if (options.Command == "where") output.WriteLine(directory);
                else renderer.Message($"已打开：{directory}");
            });
    }

    private async Task<int> UpgradeAsync(CliOptions options, SoftwareInstance instance, bool preciseSelection,
        InventorySnapshot snapshot, CancellationToken cancellationToken)
    {
        if (instance.Directories.Count > 0 && !instance.Directories.Any(directoryExists))
            return Denied(new(false, "installationDirectoryMissing", "记录的安装目录均已失效，请先确认安装状态。"), instance, snapshot, options);
        var effectiveVia = options.Via;
        var decision = Plan(instance, effectiveVia, snapshot);
        var originConfirmation = decision.Code == "originUnknown";
        if (originConfirmation)
        {
            // This is a proposal, not an ownership change. All structural guards run before prompting.
            effectiveVia = PackageManagerKind.WinGet;
            decision = Plan(instance, effectiveVia, snapshot);
        }
        if (!decision.Allowed) return Denied(decision, instance, snapshot, options);
        var confirmationRequired = originConfirmation || !preciseSelection;
        if (!options.DryRun && confirmationRequired && !CanInteract(options))
            return Denied(new(false, originConfirmation ? "originUnknown" : "targetConfirmationRequired",
                originConfirmation ? "安装来源未知。非交互升级请显式指定 --via winget；可先用 --dry-run 查看目标。"
                    : "非精确匹配需要确认。非交互升级请使用完整名称、包 ID 或实例 ID 精确选择。"), instance, snapshot, options);
        var adapter = adapters.SingleOrDefault(a => a.Manager == decision.Target!.Manager)
            ?? throw new InvalidOperationException("升级管理器适配器不可用。");
        var command = adapter.BuildCommand(decision.Target!, !CanInteract(options));
        var previewDecision = confirmationRequired ? decision with
        { Allowed = false, Code = "confirmationRequired", Reason = "升级目标已解析，执行前需要用户确认。" } : decision;
        var preview = new UpgradePreview(instance, previewDecision, command, confirmationRequired,
            instance.Owners.Count == 0, options.DryRun);
        if (options.DryRun)
            return Emit(options, 0, new { instanceId = instance.Id, decision = previewDecision, command, dryRun = true, confirmationRequired },
                snapshot.Diagnostics, snapshot.Partial, () => renderer.Upgrade(preview));
        if (confirmationRequired)
        {
            renderer.Upgrade(preview);
            if (!await interaction!.ConfirmUpgradeAsync(preview, cancellationToken)) return Cancelled(options, snapshot);
        }

        var fresh = await CollectAsync(options, "正在复核升级目标…", cancellationToken);
        var reselected = fresh.Instances.SingleOrDefault(i => Identity.Same(i.Id, instance.Id));
        if (reselected is null || !UpgradePlanner.SameInstallation(instance, reselected))
            return Denied(new(false, "installationChanged", "安装记录已变化，请重新查询后再升级。"), instance, fresh, options);
        var rechecked = Plan(reselected, effectiveVia, fresh);
        if (!rechecked.Allowed || rechecked.Target != decision.Target)
            return Denied(rechecked.Allowed ? new(false, "targetChanged", "升级目标已变化，请重新查询。") : rechecked, instance, fresh, options);
        if (reselected.Directories.Count > 0 && !reselected.Directories.Any(directoryExists))
            return Denied(new(false, "installationDirectoryMissing", "安装目录在复核时已失效，拒绝执行。"), instance, fresh, options);
        command = adapter.BuildCommand(rechecked.Target!, !CanInteract(options));
        if (!options.Json && !confirmationRequired)
            renderer.Upgrade(new(instance, rechecked, command, false, instance.Owners.Count == 0, false));
        var result = await runner.RunAsync(command, null, !options.Json, cancellationToken);
        var exit = result.Cancelled ? ExitCodes.Cancelled : result.ExitCode == 0 && !result.TimedOut ? 0 : ExitCodes.ExternalFailure;
        var diagnostics = fresh.Diagnostics.ToList();
        if (exit != 0) diagnostics.Add(new("process", result.Cancelled ? "cancelled" : "externalFailure",
            $"管理器未成功完成；原始退出码：{result.ExitCode?.ToString() ?? "未知"}。", DiagnosticSeverity.Error));
        return Emit(options, exit, new { instanceId = instance.Id, decision = rechecked, command, dryRun = false, process = result },
            diagnostics, fresh.Partial, () => renderer.Message(exit == 0 ? "管理器执行完成。" : "请检查管理器输出。"));
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

    private int Denied(UpgradeDecision decision, SoftwareInstance instance, InventorySnapshot snapshot, CliOptions options) =>
        Emit(options, ExitCodes.Undetermined, new { instanceId = instance.Id, decision },
            snapshot.Diagnostics.Append(new Diagnostic("upgrade", decision.Code, decision.Reason, DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);

    private int Cancelled(CliOptions options, InventorySnapshot snapshot) =>
        Emit(options, ExitCodes.Cancelled, null,
            snapshot.Diagnostics.Append(new Diagnostic("cli", "cancelled", "已取消，未执行操作。", DiagnosticSeverity.Error)).ToArray(), snapshot.Partial);

    private int Emit(CliOptions options, int exit, object? data, IReadOnlyList<Diagnostic> diagnostics, bool partial, Action? human = null)
    {
        if (options.Json) output.WriteLine(JsonSerializer.Serialize(new ResponseEnvelope(1, data, diagnostics, partial, exit), Output.JsonOptions));
        else
        {
            human?.Invoke();
            renderer.Diagnostics(diagnostics, options.Verbose, options.Command == "doctor");
        }
        return exit;
    }
}
