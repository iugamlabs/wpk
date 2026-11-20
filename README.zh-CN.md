[English](README.md) | [简体中文](README.zh-CN.md)

# WPK — Windows Package Kit

轻量级 Windows 软件查询、定位与升级工具。WPK 汇总 Scoop、WinGet 和 Windows 注册表中的安装记录，提供统一的命令行入口。

## 主要功能

- 按名称或 ID 搜索已安装软件，查看详情。
- 定位安装目录，或用资源管理器打开。
- 通过 Scoop 或 WinGet 升级软件，支持执行前预览。
- 将软件清单导出为 JSON，诊断不可用的信息来源。

## 运行要求

- Windows x64。框架依赖版需要 .NET 10 Runtime。
- Scoop 和 WinGet 均为可选项，缺少其中一个时，其他可用来源仍可工作。
- CLI 帮助和面向用户的输出目前为中文。

## 安装

### Scoop

```powershell
scoop bucket add iugamlabs https://github.com/iugamlabs/scoop
scoop install wpk
wpk --version
```

Scoop 安装 Windows x64 Native AOT 版。后续可用 `scoop update wpk` 升级。

### 手动下载

从 [GitHub Releases](https://github.com/iugamlabs/wpk/releases) 下载 **win-x64** 构建：

| 产物 | 模式 | 说明 |
| --- | --- | --- |
| `wpk-{version}-win-x64.exe` | 框架依赖 | 需要本机 [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；FD 无法 IL 裁剪 |
| `wpk-{version}-win-x64-sc.exe` | 自包含 + 裁剪 | 内嵌运行时；通常最省事的单文件安装 |
| `wpk-{version}-win-x64-r2r.exe` | 自包含 + 裁剪 + ReadyToRun | 启动更快，体积大于 `-sc` |
| `wpk-{version}-win-x64-aot.zip` | Native AOT | 内含 WinGet DLL 和 WinMD。将三个文件解压到同一目录，无需 .NET 运行时。 |

`.exe` 产物请重命名为 `wpk.exe` 并放到 `PATH`。要采集 WinGet 清单，请从同版本的 AOT ZIP 复制 `Microsoft.Management.Deployment.winmd` 到 EXE 同目录。用 `wpk --version` 验证安装。校验和见 `SHA256SUMS.txt`。

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
