[English](README.md) | [简体中文](README.zh-CN.md)

# WPK — Windows Package Kit

轻量级 Windows 软件查询、定位与升级工具。WPK 汇总 Scoop、WinGet 和 Windows 注册表中的安装记录，提供统一的命令行入口。

## 主要功能

- 按名称或 ID 搜索已安装软件，查看详情。
- 定位安装目录，或用资源管理器打开。
- 通过 Scoop 或 WinGet 升级软件，支持执行前预览。
- 将软件清单导出为 JSON，诊断不可用的信息来源。

## 运行要求

- Windows x64 和 .NET 10 Runtime；其他架构尚未验收。
- Scoop 和 WinGet 均为可选项，缺少其中一个时，其他可用来源仍可工作。
- CLI 帮助和面向用户的输出目前为中文。

## 安装

## 使用

| 命令 | 说明 |
| --- | --- |
| `wpk <name>` | 显示软件详情或匹配的候选项 |
| `wpk find <name>` | 搜索已安装软件 |
| `wpk ls` | 列出已安装软件 |
| `wpk where <name>` | 输出安装目录 |
| `wpk open <name>` | 用资源管理器打开安装目录 |
| `wpk up <name>` | 委托合适的管理器升级；别名：`update`、`upgrade` |
| `wpk doctor` | 检查环境和信息来源 |

```powershell
wpk find git
wpk where 7zip --exact
wpk up typora --dry-run
wpk ls --json
wpk --help
```

使用 `--exact` 匹配完整名称或 ID，`--scope user|machine` 按安装范围筛选，`--verbose` 查看 ID 和诊断。完整选项见[使用参考](docs/reference.md)。

## 升级前须知

- WPK 搜索的是**已安装软件**，不搜索远程软件目录。目前不支持安装、卸载或批量升级。
- **WinGet 能识别某个软件，不代表它最初由 WinGet 安装。** WPK 分别展示识别关系和已确认的安装归属，无法证明的归属保持未知。
- 使用 `--dry-run` 预览目标和命令，不执行升级。WPK 可能要求选择安装实例或确认管理器；目标不明确或存在冲突时会拒绝升级。
- 没有安装记录的软件可能不在清单内，部分安装目录也可能未知。可运行 `wpk doctor` 排查信息来源问题。

## 详细文档

以下详细文档目前为中文：

- [使用参考](docs/reference.md)：完整选项、匹配、升级规则、JSON、退出码和已知限制。
- [设计说明](docs/design.md)：信息来源、安装归属、去重和架构。
- [开发指南](docs/development.md)：构建、测试、发布和验证。
