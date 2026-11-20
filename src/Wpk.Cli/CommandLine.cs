using System.Reflection;
using ConsoleAppFramework;
using Wpk.Core;

namespace Wpk.Cli;

public sealed record CliOptions(string Command, string? Query, bool Json, bool Exact,
    InstallScope? Scope, PackageManagerKind? Via, bool DryRun, bool Verbose = false, bool All = false);

public sealed record CommandRunResult(int ExitCode, string Text, bool Executed, bool Version);

internal sealed record GlobalFlags(bool Json, bool Verbose);

/// <summary>
/// ConsoleAppFramework owns parsing and help/version.
/// Normalize only maps the product shortcut <c>wpk &lt;name&gt;</c> to <c>query</c>.
/// </summary>
public static class CommandLine
{
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(CommandLine).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return typeof(CommandLine).Assembly.GetName().Version!.ToString(3);
    }

    private static readonly HashSet<string> Commands =
        ["query", "find", "where", "open", "ls", "up", "update", "upgrade", "doctor"];

    public static bool HasFlag(string[] args, string flag) =>
        args.TakeWhile(a => a != "--").Contains(flag);

    public static async Task<CommandRunResult> InvokeAsync(string[] args,
        Func<CliOptions, CancellationToken, Task<int>> execute, CancellationToken cancellationToken)
    {
        var dispatch = new CommandDispatch(execute);
        var capture = new StringWriter();
        var previousLog = ConsoleApp.Log;
        var previousError = ConsoleApp.LogError;
        var previousVersion = ConsoleApp.Version;
        var previousExit = Environment.ExitCode;
        ConsoleApp.Log = msg => capture.WriteLine(msg);
        ConsoleApp.LogError = msg => capture.WriteLine(msg);
        ConsoleApp.Version = Version;

        var isVersion = HasFlag(args, "--version");
        var routed = Normalize(args);

        CommandGate.Dispatch.Value = dispatch;
        try
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            Environment.ExitCode = 0;
            var app = ConsoleApp.Create();
            app.ConfigureGlobalOptions((ref ConsoleApp.GlobalOptionsBuilder builder) =>
            {
                var json = builder.AddGlobalOption<bool>("--json", "输出版本化 JSON，禁用交互");
                var verbose = builder.AddGlobalOption<bool>("-v|--verbose", "显示实例 ID、原始证据和完整诊断");
                return new GlobalFlags(json, verbose);
            });
            app.Add<WpkCommands>();
            await app.RunAsync(routed, cancellationToken);
            var exit = dispatch.Executed ? dispatch.LastExitCode : Environment.ExitCode;
            return new(exit, capture.ToString(), dispatch.Executed, isVersion);
        }
        finally
        {
            CommandGate.Dispatch.Value = null;
            ConsoleApp.Log = previousLog;
            ConsoleApp.LogError = previousError;
            ConsoleApp.Version = previousVersion;
            Environment.ExitCode = previousExit;
        }
    }

    /// <summary>
    /// CAF routes on <c>args[0]</c>. Map a bare name to <c>query</c>; leave real commands alone.
    /// Command must come first — put options after the command name.
    /// </summary>
    private static string[] Normalize(string[] args)
    {
        if (args.Length == 0) return args;
        if (args[0] == "--") return ["query", .. args];
        if (args[0].StartsWith('-')) return args;
        return Commands.Contains(args[0]) ? args : ["query", .. args];
    }
}

internal static class CommandGate
{
    public static readonly AsyncLocal<CommandDispatch?> Dispatch = new();

    public static CommandDispatch RequireDispatch() =>
        Dispatch.Value ?? throw new InvalidOperationException("命令调度器未初始化。");
}

public sealed class CommandDispatch(Func<CliOptions, CancellationToken, Task<int>> execute)
{
    public bool Executed { get; private set; }
    public int LastExitCode { get; private set; }

    public async Task<int> ExecuteAsync(CliOptions options, CancellationToken cancellationToken)
    {
        Executed = true;
        return LastExitCode = await execute(options, cancellationToken);
    }
}

/// <summary>WPK commands. XML docs drive ConsoleAppFramework help text.</summary>
internal sealed class WpkCommands
{
    /// <summary>查询详情；也可直接使用 wpk &lt;name&gt;</summary>
    /// <param name="name">软件名称、包 ID 或实例 ID</param>
    /// <param name="exact">只匹配完整名称或 ID</param>
    /// <param name="scope">安装范围：user 或 machine</param>
    [Command("query")]
    public Task<int> Query(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        bool exact = false,
        string? scope = null,
        [Argument] string? name = null) =>
        Lookup("query", name, exact, scope, context, cancellationToken);

    /// <summary>查找软件安装记录</summary>
    /// <param name="name">软件名称、包 ID 或实例 ID；多词名称请加引号</param>
    /// <param name="exact">只匹配完整名称或 ID</param>
    /// <param name="scope">安装范围：user 或 machine</param>
    [Command("find")]
    public Task<int> Find(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        bool exact = false,
        string? scope = null,
        [Argument] string? name = null) =>
        Lookup("find", name, exact, scope, context, cancellationToken);

    /// <summary>定位安装目录</summary>
    /// <param name="name">软件名称、包 ID 或实例 ID；多词名称请加引号</param>
    /// <param name="exact">只匹配完整名称或 ID</param>
    /// <param name="scope">安装范围：user 或 machine</param>
    [Command("where")]
    public Task<int> Where(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        bool exact = false,
        string? scope = null,
        [Argument] string? name = null) =>
        Lookup("where", name, exact, scope, context, cancellationToken);

    /// <summary>打开安装目录</summary>
    /// <param name="name">软件名称、包 ID 或实例 ID；多词名称请加引号</param>
    /// <param name="exact">只匹配完整名称或 ID</param>
    /// <param name="scope">安装范围：user 或 machine</param>
    [Command("open")]
    public Task<int> Open(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        bool exact = false,
        string? scope = null,
        [Argument] string? name = null) =>
        Lookup("open", name, exact, scope, context, cancellationToken);

    /// <summary>按安装归属列出已安装软件，默认隐藏归属未知的实例</summary>
    /// <param name="scope">安装范围：user 或 machine</param>
    /// <param name="all">-a, 显示全部安装实例，包括归属未知的实例</param>
    [Command("ls")]
    public Task<int> List(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        string? scope = null,
        bool all = false)
    {
        RejectExtras("ls", context);
        var flags = Flags(context);
        return CommandGate.RequireDispatch().ExecuteAsync(
            new("ls", null, flags.Json, false, ParseScope(scope), null, false, flags.Verbose, All: all),
            cancellationToken);
    }

    /// <summary>升级软件；来源未知时先确认管理器</summary>
    /// <param name="name">软件名称、包 ID 或实例 ID；多词名称请加引号</param>
    /// <param name="exact">只匹配完整名称或 ID</param>
    /// <param name="scope">安装范围：user 或 machine</param>
    /// <param name="via">明确选择 scoop 或 winget，仅授权本次升级</param>
    /// <param name="dryRun">只预览目标和命令，不确认或执行</param>
    [Command("up|update|upgrade")]
    public Task<int> Upgrade(
        ConsoleAppContext context,
        CancellationToken cancellationToken,
        bool exact = false,
        string? scope = null,
        string? via = null,
        bool dryRun = false,
        [Argument] string? name = null)
    {
        var query = RequireName("up", name, context);
        var flags = Flags(context);
        return CommandGate.RequireDispatch().ExecuteAsync(
            new("up", query, flags.Json, exact, ParseScope(scope), ParseVia(via), dryRun, flags.Verbose),
            cancellationToken);
    }

    /// <summary>检查运行环境和信息来源</summary>
    [Command("doctor")]
    public Task<int> Doctor(ConsoleAppContext context, CancellationToken cancellationToken)
    {
        // CAF emits an empty parse loop for parameterless commands; reject leftover tokens ourselves.
        if (!context.InternalCommandArgs.IsEmpty || context.EscapedArguments.Length > 0)
            throw new ArgumentParseFailedException("doctor 不接受额外参数。");
        var flags = Flags(context);
        return CommandGate.RequireDispatch().ExecuteAsync(
            new("doctor", null, flags.Json, false, null, null, false, flags.Verbose),
            cancellationToken);
    }

    private static Task<int> Lookup(string command, string? name, bool exact, string? scope,
        ConsoleAppContext context, CancellationToken cancellationToken)
    {
        var query = RequireName(command, name, context);
        var flags = Flags(context);
        return CommandGate.RequireDispatch().ExecuteAsync(
            new(command, query, flags.Json, exact, ParseScope(scope), null, false, flags.Verbose),
            cancellationToken);
    }

    private static GlobalFlags Flags(ConsoleAppContext context) =>
        context.GlobalOptions as GlobalFlags ?? new GlobalFlags(false, false);

    private static string RequireName(string command, string? name, ConsoleAppContext context)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(name)) parts.Add(name.Trim());
        parts.AddRange(context.EscapedArguments);
        if (parts.Count == 0)
            throw new ArgumentParseFailedException($"{command} 缺少软件名称。");
        return string.Join(' ', parts);
    }

    private static void RejectExtras(string command, ConsoleAppContext context)
    {
        if (context.EscapedArguments.Length > 0)
            throw new ArgumentParseFailedException($"{command} 不接受软件名称。");
    }

    private static InstallScope? ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        null => null,
        "user" => InstallScope.User,
        "machine" => InstallScope.Machine,
        _ => throw new ArgumentParseFailedException("--scope 必须为 user 或 machine。")
    };

    private static PackageManagerKind? ParseVia(string? via) => via?.ToLowerInvariant() switch
    {
        null => null,
        "scoop" => PackageManagerKind.Scoop,
        "winget" => PackageManagerKind.WinGet,
        _ => throw new ArgumentParseFailedException("--via 必须为 scoop 或 winget。")
    };
}
