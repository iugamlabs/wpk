using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Help;
using Spectre.Console.Rendering;
using Wpk.Core;

namespace Wpk.Cli;

public sealed record CliOptions(string Command, string? Query, bool Json, bool Exact,
    InstallScope? Scope, PackageManagerKind? Via, bool DryRun, bool Verbose = false);
public sealed record CommandRunResult(int ExitCode, string Text, bool Executed, bool Version);

/// <summary>The only compatibility routing is the implicit query; Spectre owns parsing and validation.</summary>
public static class CommandLine
{
    public static string Version { get; } = typeof(CommandLine).Assembly.GetName().Version!.ToString(3);
    private static readonly HashSet<string> Commands = ["query", "find", "where", "open", "ls", "up", "update", "upgrade", "doctor"];

    public static bool HasFlag(string[] args, string flag) => args.TakeWhile(a => a != "--").Contains(flag);

    public static async Task<CommandRunResult> InvokeAsync(string[] args,
        Func<CliOptions, CancellationToken, Task<int>> execute, CancellationToken cancellationToken)
    {
        using var capture = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        { Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors, Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(capture) });
        console.Profile.Width = 100;
        var dispatch = new CommandDispatch(execute);
        var registrar = new CommandRegistrar();
        registrar.RegisterInstance(typeof(CommandDispatch), dispatch);
        var app = new CommandApp(registrar);
        app.Configure(config =>
        {
            config.SetApplicationName("wpk");
            config.SetApplicationVersion(Version);
            config.ConfigureConsole(console);
            config.PropagateExceptions();
            config.Settings.StrictParsing = true;
            config.Settings.Culture = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
            config.SetHelpProvider(new WpkHelpProvider(config.Settings));
            config.AddCommand<QueryCommand>("query").WithDescription("查询详情；也可直接使用 wpk <name>").WithExample("query", "typora");
            config.AddCommand<FindCommand>("find").WithDescription("查找软件安装记录").WithExample("find", "git");
            config.AddCommand<WhereCommand>("where").WithDescription("定位安装目录");
            config.AddCommand<OpenCommand>("open").WithDescription("打开安装目录");
            config.AddCommand<ListCommand>("ls").WithDescription("汇总已安装软件");
            config.AddCommand<UpgradeCommand>("up").WithAlias("update").WithAlias("upgrade")
                .WithDescription("升级软件；来源未知时先确认管理器").WithExample("up", "typora", "--dry-run");
            config.AddCommand<DoctorCommand>("doctor").WithDescription("检查运行环境和信息来源");
        });
        var isHelp = HasFlag(args, "--help") || HasFlag(args, "-h") || args.All(a => a is "--json" or "--verbose");
        var isVersion = (HasFlag(args, "--version") || HasFlag(args, "-v")) && !isHelp;
        var routed = Normalize(args);
        if (isHelp || isVersion) routed = RemoveRenderingFlags(routed);
        var result = await app.RunAsync(routed, cancellationToken);
        return new(result, capture.ToString(), dispatch.Executed, isVersion);
    }

    private static string[] RemoveRenderingFlags(string[] args)
    {
        var beforeTerminator = true;
        return args.Where(arg =>
        {
            if (arg == "--") { beforeTerminator = false; return true; }
            return !beforeTerminator || arg is not ("--json" or "--verbose");
        }).ToArray();
    }

    private static string[] Normalize(string[] args)
    {
        if (args.All(a => a is "--json" or "--verbose")) return ["--help", .. args];
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--") return ["query", .. args];
            if (args[i] is "--scope" or "--via") { i++; continue; }
            if (args[i].StartsWith('-')) continue;
            return Commands.Contains(args[i])
                ? [args[i], .. args.Where((_, index) => index != i)] : ["query", .. args];
        }
        return HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "--version") || HasFlag(args, "-v")
            ? args : ["query", .. args];
    }
}

public class CommonSettings : CommandSettings
{
    [CommandOption("--json"), Description("输出版本化 JSON，禁用交互")]
    public bool Json { get; set; }
    [CommandOption("--verbose"), Description("显示实例 ID、原始证据和完整诊断")]
    public bool Verbose { get; set; }
}

public class ScopedSettings : CommonSettings
{
    [CommandOption("--scope <SCOPE>"), Description("安装范围：user 或 machine")]
    public string? Scope { get; set; }
    public InstallScope? ParsedScope => Scope?.ToLowerInvariant() switch
    { "user" => InstallScope.User, "machine" => InstallScope.Machine, _ => null };
    public override ValidationResult Validate() => Scope is not null && ParsedScope is null
        ? ValidationResult.Error("--scope 必须为 user 或 machine。") : ValidationResult.Success();
}

public class LookupSettings : ScopedSettings
{
    // Spectre puts values after '--' in Remaining.Raw. The command validates the combined required name.
    [CommandArgument(0, "[name]"), Description("软件名称、包 ID 或实例 ID（必填）；支持多词名称")]
    public string[] Name { get; set; } = [];
    [CommandOption("--exact"), Description("只匹配完整名称或 ID")]
    public bool Exact { get; set; }
    public virtual CliOptions Options(string command) => new(command, string.Join(' ', Name), Json, Exact, ParsedScope, null, false, Verbose);
}

internal sealed class WpkHelpProvider(ICommandAppSettings settings) : HelpProvider(settings)
{
    public override IEnumerable<IRenderable> GetFooter(ICommandModel model, ICommandInfo? command)
    {
        yield return new Text("\n快捷查询：wpk <name>；带名称的命令必须提供名称、包 ID 或实例 ID。\n");
        yield return new Text("通用选项：--json 输出 JSON；--verbose 显示实例 ID 和完整诊断。\n");
        yield return new Text("升级别名：up / update / upgrade；可先用 --dry-run 查看目标。\n");
    }
}

public sealed class UpgradeSettings : LookupSettings
{
    [CommandOption("--via <MANAGER>"), Description("明确选择 scoop 或 winget，仅授权本次升级")]
    public string? Via { get; set; }
    [CommandOption("--dry-run"), Description("只预览目标和命令，不确认或执行")]
    public bool DryRun { get; set; }
    public override ValidationResult Validate() => Via is not null && Via.ToLowerInvariant() is not ("scoop" or "winget")
        ? ValidationResult.Error("--via 必须为 scoop 或 winget。") : base.Validate();
    public override CliOptions Options(string command) => base.Options(command) with
    { Via = Via?.ToLowerInvariant() switch { "scoop" => PackageManagerKind.Scoop, "winget" => PackageManagerKind.WinGet, _ => null }, DryRun = DryRun };
}

public sealed class CommandDispatch(Func<CliOptions, CancellationToken, Task<int>> execute)
{
    public bool Executed { get; private set; }
    public Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken)
    { Executed = true; return execute(options, cancellationToken); }
}

public abstract class LookupCommand<T>(CommandDispatch dispatch) : AsyncCommand<T> where T : LookupSettings
{
    protected abstract string Name { get; }
    public override Task<int> ExecuteAsync(CommandContext context, T settings, CancellationToken cancellationToken)
    {
        settings.Name = [.. settings.Name, .. context.Remaining.Raw];
        if (settings.Name.Length == 0 || string.IsNullOrWhiteSpace(string.Join(' ', settings.Name)))
            throw new ArgumentException($"{Name} 缺少软件名称。");
        return dispatch.ExecuteAsync(settings.Options(Name), cancellationToken);
    }
}
public sealed class QueryCommand(CommandDispatch dispatch) : LookupCommand<LookupSettings>(dispatch) { protected override string Name => "query"; }
public sealed class FindCommand(CommandDispatch dispatch) : LookupCommand<LookupSettings>(dispatch) { protected override string Name => "find"; }
public sealed class WhereCommand(CommandDispatch dispatch) : LookupCommand<LookupSettings>(dispatch) { protected override string Name => "where"; }
public sealed class OpenCommand(CommandDispatch dispatch) : LookupCommand<LookupSettings>(dispatch) { protected override string Name => "open"; }
public sealed class UpgradeCommand(CommandDispatch dispatch) : LookupCommand<UpgradeSettings>(dispatch) { protected override string Name => "up"; }
public sealed class ListCommand(CommandDispatch dispatch) : AsyncCommand<ScopedSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, ScopedSettings settings, CancellationToken cancellationToken)
    {
        if (context.Remaining.Raw.Count > 0) throw new ArgumentException("ls 不接受软件名称。");
        return dispatch.ExecuteAsync(new("ls", null, settings.Json, false, settings.ParsedScope, null, false, settings.Verbose), cancellationToken);
    }
}
public sealed class DoctorCommand(CommandDispatch dispatch) : AsyncCommand<CommonSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, CommonSettings settings, CancellationToken cancellationToken)
    {
        if (context.Remaining.Raw.Count > 0) throw new ArgumentException("doctor 不接受软件名称。");
        return dispatch.ExecuteAsync(new("doctor", null, settings.Json, false, null, null, false, settings.Verbose), cancellationToken);
    }
}

internal sealed class CommandRegistrar : ITypeRegistrar, ITypeResolver
{
    private readonly Dictionary<Type, List<Func<object>>> factories = [];
    private void Add(Type service, Func<object> factory)
    {
        if (!factories.TryGetValue(service, out var values)) factories[service] = values = [];
        values.Add(factory);
    }
    public void Register(Type service, Type implementation) => Add(service, () =>
    {
        var constructor = implementation.GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        return constructor.Invoke(constructor.GetParameters().Select(p => Resolve(p.ParameterType)
            ?? (p.HasDefaultValue ? p.DefaultValue : throw new InvalidOperationException($"未注册命令依赖：{p.ParameterType}"))).ToArray());
    });
    public void RegisterInstance(Type service, object implementation) => Add(service, () => implementation);
    public void RegisterLazy(Type service, Func<object> factory) => Add(service, factory);
    public ITypeResolver Build() => this;
    public object? Resolve(Type? type)
    {
        if (type is null) return null;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            var element = type.GetGenericArguments()[0];
            var values = factories.TryGetValue(element, out var found) ? found : [];
            var array = Array.CreateInstance(element, values.Count);
            for (var i = 0; i < values.Count; i++) array.SetValue(values[i](), i);
            return array;
        }
        return factories.TryGetValue(type, out var factoriesForType) ? factoriesForType[^1]() : null;
    }
}
