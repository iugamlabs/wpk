using Wpk.Core;

namespace Wpk.Cli;

public sealed record CliOptions(string Command, string? Query, bool Json, bool Exact,
    InstallScope? Scope, PackageManagerKind? Via, bool DryRun);

public static class CommandLine
{
    private static readonly HashSet<string> Commands = ["find", "where", "open", "ls", "up", "doctor"];

    public static CliOptions Parse(string[] args)
    {
        var words = new List<string>();
        var json = false; var exact = false; var dry = false;
        InstallScope? scope = null;
        PackageManagerKind? via = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var positionalOnly = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (positionalOnly || !arg.StartsWith('-')) { words.Add(arg); continue; }
            if (arg == "--") { positionalOnly = true; continue; }
            if (!seen.Add(arg)) throw new ArgumentException($"选项重复：{arg}");
            string Value() => ++i < args.Length && !args[i].StartsWith('-') ? args[i]
                : throw new ArgumentException($"{arg} 缺少参数。");
            switch (arg)
            {
                case "-h": case "--help": return new("help", null, args.Contains("--json"), false, null, null, false);
                case "--version": return new("version", null, args.Contains("--json"), false, null, null, false);
                case "--json": json = true; break;
                case "--exact": exact = true; break;
                case "--dry-run": dry = true; break;
                case "--scope": scope = Value().ToLowerInvariant() switch
                    { "user" => InstallScope.User, "machine" => InstallScope.Machine, _ => throw new ArgumentException("--scope 必须为 user 或 machine。") }; break;
                case "--via": via = Value().ToLowerInvariant() switch
                    { "scoop" => PackageManagerKind.Scoop, "winget" => PackageManagerKind.WinGet, _ => throw new ArgumentException("--via 必须为 scoop 或 winget。") }; break;
                default: throw new ArgumentException($"未知选项：{arg}");
            }
        }
        if (words.Count == 0)
        {
            if (exact || scope.HasValue || via.HasValue || dry) throw new ArgumentException("缺少命令或软件名称。");
            return new("help", null, json, false, null, null, false);
        }
        var command = Commands.Contains(words[0]) ? words[0] : "query";
        var query = string.Join(' ', command == "query" ? words : words.Skip(1));
        if (command is "ls" or "doctor")
        {
            if (query.Length > 0) throw new ArgumentException($"{command} 不接受软件名称。");
            if (exact) throw new ArgumentException($"{command} 不接受 --exact。");
            if (command == "doctor" && scope.HasValue) throw new ArgumentException("doctor 不接受 --scope。");
        }
        else if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException($"{command} 缺少软件名称。");
        if (command != "up" && (via.HasValue || dry)) throw new ArgumentException("--via 和 --dry-run 仅适用于 up。");
        return new(command, query.Length == 0 ? null : query, json, exact, scope, via, dry);
    }

    public const string Help = """
        WPK — Windows Package Kit
        用法：wpk <name> | wpk <command> [name] [options]

          <name>          查询安装详情；多个结果时列出候选
          find <name>     查找安装记录（支持名称、原生包 ID、实例 ID 和模糊搜索）
          where <name>    输出唯一安装实例的已知目录
          open <name>     打开唯一安装实例的目录
          ls              列出已安装软件
          up <name>       委托安装管理器升级唯一实例
          doctor          检查运行环境、管理器和信息来源

          --json          输出版本化 JSON；子进程输出包含在 JSON 内
          --exact         仅匹配完整名称、原生包 ID 或实例 ID
          --scope <user|machine>  按安装范围筛选
          --via <scoop|winget>    显式选择已有识别关系的管理器（仅 up）
          --dry-run       返回升级决策和命令，不启动升级（仅 up）
          -h, --help      帮助
          --version       版本
          --              后续内容作为查询；例如 wpk find -- ls

        多个结果时，请复制候选的实例 ID，或用 --exact / --scope 缩小范围。
        WinGet 识别不代表由 WinGet 安装。来源未知时须显式指定 --via winget。
        不自动提权、不自动接受协议、不按搜索排名自动选择升级目标。
        """;
}
