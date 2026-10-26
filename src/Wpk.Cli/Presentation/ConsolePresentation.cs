using Spectre.Console;
using Wpk.Cli.Platform;
using Wpk.Core;

namespace Wpk.Cli.Presentation;

public sealed record UpgradePreview(SoftwareInstance Instance, UpgradeDecision Decision, CommandSpec Command,
    bool ConfirmationRequired, bool OriginUnknown, bool DryRun);

public interface ICliRenderer
{
    void List(IReadOnlyList<SoftwareInstance> instances, bool showIds);
    void Details(SoftwareInstance instance, bool verbose);
    void Doctor(DoctorReport report);
    void Upgrade(UpgradePreview preview);
    void Message(string text);
    void Diagnostics(IReadOnlyList<Diagnostic> diagnostics, bool verbose, bool doctor);
    Task<T> StatusAsync<T>(string text, Func<Task<T>> action, bool enabled);
}

public interface IUserInteraction
{
    bool IsInteractive { get; }
    Task<SoftwareInstance?> SelectAsync(IReadOnlyList<SoftwareInstance> candidates, CancellationToken cancellationToken);
    Task<bool> ConfirmUpgradeAsync(UpgradePreview preview, CancellationToken cancellationToken);
}

public sealed class TerminalContext
{
    public IAnsiConsole Output { get; }
    public IAnsiConsole Error { get; }
    public bool Interactive { get; }
    public bool Animated { get; }

    public TerminalContext(TextWriter output, TextWriter error, bool terminal = false, bool interactive = false, bool noColor = false)
    {
        Interactive = terminal && interactive;
        Output = Create(output, terminal, Interactive, noColor);
        Error = Create(error, terminal, Interactive, noColor);
        Animated = terminal && Error.Profile.Capabilities.Ansi;
    }

    private static IAnsiConsole Create(TextWriter writer, bool terminal, bool interactive, bool noColor) =>
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer), Ansi = terminal && !noColor ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = noColor || !terminal ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = interactive ? InteractionSupport.Yes : InteractionSupport.No
        });
}

public sealed class SpectreRenderer(IAnsiConsole console, IAnsiConsole error, bool animated = false) : ICliRenderer
{
    private static Text T(string? value, Style? style = null) => new(Output.Safe(value ?? "未知"), style);
    private static string Managers(SoftwareInstance i) => string.Join(", ", i.Records
        .Where(r => r.Source != SourceKind.Registry).Select(r => r.Source.ToString().ToLowerInvariant()).Distinct().DefaultIfEmpty("未识别"));

    public void List(IReadOnlyList<SoftwareInstance> instances, bool showIds)
    {
        if (console.Profile.Width < 70)
        {
            foreach (var i in instances)
            {
                console.Write(T($"{i.Name}  {i.Version ?? "未知"}", new Style(Color.Cyan1)));
                console.WriteLine();
                console.Write(T($"  {Output.Scope(i.Scope)} · 归属 {Output.Owner(i)} · 识别 {Managers(i)}"));
                console.WriteLine();
            }
        }
        else
        {
            var table = new Table().Border(TableBorder.Simple).Expand();
            foreach (var label in new[] { "软件", "版本", "范围", "安装归属", "管理器识别" }) table.AddColumn(new TableColumn(T(label, new Style(decoration: Decoration.Bold))));
            foreach (var i in instances)
                table.AddRow(T(i.Name, new Style(Color.Cyan1)), T(i.Version), T(Output.Scope(i.Scope)),
                    T(Output.Owner(i), new Style(i.Owners.Count == 0 ? Color.Grey : Color.Green)), T(Managers(i)));
            console.Write(table);
        }
        if (showIds)
        {
            foreach (var i in instances)
            {
                console.Write(T($"{i.Name}: {i.Id}")); console.WriteLine();
            }
        }
    }

    public void Details(SoftwareInstance instance, bool verbose)
    {
        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow(T("版本"), T(instance.Version));
        grid.AddRow(T("范围"), T(Output.Scope(instance.Scope)));
        grid.AddRow(T("安装归属"), T(Output.Owner(instance)));
        grid.AddRow(T("管理器识别"), T(Managers(instance)));
        grid.AddRow(T("目录"), T(string.Join("；", instance.Directories.DefaultIfEmpty("未知"))));
        grid.AddRow(T("实例 ID"), T(instance.Id));
        console.Write(new Panel(grid).Header(Markup.Escape(Output.Safe(instance.Name)))
            .Border(console.Profile.Capabilities.Unicode ? BoxBorder.Rounded : BoxBorder.Ascii));
        if (verbose)
        {
            foreach (var record in instance.Records)
            {
                Message($"{record.Source.ToString().ToLowerInvariant()}: {record.NativeId} ({record.Id})");
                foreach (var e in record.Evidence) Message($"  {e.Description} ({e.Location})");
            }
        }
    }

    public void Doctor(DoctorReport report)
    {
        var table = new Table().Border(TableBorder.Simple).AddColumn("状态").AddColumn("检查项").AddColumn("说明");
        foreach (var c in report.Checks)
            table.AddRow(T(c.Status, new Style(c.Status == "ok" ? Color.Green : c.Status == "error" ? Color.Red : Color.Yellow)), T(c.Name), T(c.Detail));
        console.Write(table);
        Message($"安装实例：{report.InstalledInstances}");
    }

    public void Upgrade(UpgradePreview preview)
    {
        var target = preview.Decision.Target!;
        Message($"{preview.Instance.Name}  {preview.Instance.Version ?? "版本未知"}");
        Message($"升级方式：{target.Manager} · {target.PackageId} · {Output.Scope(target.Scope)} · {target.Repository ?? "默认仓库"}");
        Message(preview.OriginUnknown ? "安装来源未知；WinGet 识别不证明历史安装渠道。" : preview.Decision.Reason);
        if (preview.DryRun) Message(preview.ConfirmationRequired ? "预览：执行前需要确认，尚未执行。" : "预览：尚未执行。");
        Message($"程序：{preview.Command.FileName}");
        Message($"参数：{string.Join(' ', preview.Command.Arguments.Select(Quote))}");
    }

    private static string Quote(string value) => value.Any(char.IsWhiteSpace) || value.Contains('"')
        ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    public void Message(string text) { console.Write(T(text)); console.WriteLine(); }

    public void Diagnostics(IReadOnlyList<Diagnostic> diagnostics, bool verbose, bool doctor)
    {
        foreach (var d in diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error || verbose))
        {
            error.Write(T($"[{d.Source}/{d.Code}] {d.Message}", new Style(d.Severity == DiagnosticSeverity.Error ? Color.Red : Color.Yellow)));
            error.WriteLine();
        }
        var warnings = diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error).ToArray();
        if (warnings.Length == 0 || verbose || doctor) return;
        var sources = string.Join(", ", warnings.Select(d => d.Repository ?? d.Source).Distinct());
        error.Write(T($"提示：{warnings.Length} 项来源受限（{sources}）；运行 wpk doctor 或加 --verbose 查看详情。", new Style(Color.Yellow)));
        error.WriteLine();
    }

    public Task<T> StatusAsync<T>(string text, Func<Task<T>> action, bool enabled) => enabled && animated
        ? error.Status().Spinner(Spinner.Known.Dots).StartAsync(Markup.Escape(text), _ => action()) : action();
}

public sealed class SpectreInteraction(IAnsiConsole console, bool interactive) : IUserInteraction
{
    public bool IsInteractive => interactive && console.Profile.Capabilities.Interactive;
    private sealed record Choice(SoftwareInstance? Instance);

    public async Task<SoftwareInstance?> SelectAsync(IReadOnlyList<SoftwareInstance> candidates, CancellationToken cancellationToken)
    {
        if (!IsInteractive) return null;
        var prompt = new SelectionPrompt<Choice>().Title("找到多个安装实例，请选择：").PageSize(8)
            .UseConverter(c => c.Instance is not { } i ? "取消" : Markup.Escape(Output.Safe(
                $"{i.Name} | {i.Version ?? "未知"} | {Output.Scope(i.Scope)} | 归属 {Output.Owner(i)} | {string.Join(", ", i.Directories)}")))
            .AddChoices(new[] { new Choice(null) }.Concat(candidates.Select(i => new Choice(i))));
        try { return (await prompt.ShowAsync(console, cancellationToken)).Instance; }
        catch (Exception e) when (e is EndOfStreamException or InvalidOperationException) { return null; }
    }

    public async Task<bool> ConfirmUpgradeAsync(UpgradePreview preview, CancellationToken cancellationToken)
    {
        if (!IsInteractive) return false;
        var prompt = new ConfirmationPrompt(preview.OriginUnknown ? "是否通过上述 WinGet 目标升级？" : "是否升级上述匹配的软件？")
        { DefaultValue = false };
        try { return await prompt.ShowAsync(console, cancellationToken); }
        catch (Exception e) when (e is EndOfStreamException or InvalidOperationException) { return false; }
    }
}
