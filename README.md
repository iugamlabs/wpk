# WPK — Windows Package Kit

轻量级 Windows 软件管理 CLI，使用 C# / .NET 10 开发。统一查看 Scoop、WinGet 和 Windows 卸载注册表中的安装记录，并按可信的管理归属委托升级。

**v0.2.0** 使用 Spectre.Console / Spectre.Console.Cli 提供类型化命令、中文帮助、自适应表格、状态显示、候选选择和升级确认。

**WinGet 能识别某个软件，不代表它最初由 WinGet 安装。** WPK 分别展示观察来源、识别关系和安装归属；无法证明的归属保持“未知”。

## 快速开始

开发需要 Windows x64、.NET 10 SDK，以及首次还原依赖时的 NuGet 网络连接。发布目录运行需要 .NET 10 Runtime。WinGet 和 Scoop 都是可选来源：缺少其中一个时，其他来源继续工作。

```powershell
dotnet restore Wpk.slnx --locked-mode
dotnet build Wpk.slnx --no-restore
dotnet test Wpk.slnx --no-restore

dotnet run --project src/Wpk.Cli -- --help
dotnet run --project src/Wpk.Cli -- doctor
dotnet run --project src/Wpk.Cli -- find git
```

生成框架依赖的 x64 发布目录：

```powershell
dotnet publish src/Wpk.Cli -c Release -r win-x64 --self-contained false --no-restore -o artifacts/wpk-win-x64
.\artifacts\wpk-win-x64\wpk.exe --help
```

将**完整发布目录**放到固定位置，再把目录加入用户 PATH。保留其中的 DLL、WinMD、JSON 等文件；不能只复制 `wpk.exe`。不需要安装后台服务。

项目最低 Windows API 版本为 10.0.19041；运行系统仍需满足 .NET 10 的支持要求。当前发布、CI 和测试目标为 `win-x64`，其他架构尚未验收。

## 命令

| 命令 | 行为 |
| --- | --- |
| `wpk <name>` | 唯一结果显示详情，多个结果列出候选 |
| `wpk find <name>` | 搜索已安装记录；不搜索远程可安装软件目录 |
| `wpk where <name>` | 输出唯一实例的已知且存在的安装目录 |
| `wpk open <name>` | 用资源管理器打开唯一实例的安装目录 |
| `wpk ls` | 汇总合并后的安装实例 |
| `wpk up <name>` | 按管理归属选择升级方式；别名为 `update`、`upgrade` |
| `wpk doctor` | 检查环境、CLI、Scoop 根目录和各信息来源 |

```powershell
wpk vscode
wpk find "Visual Studio"
wpk find Vendor.App --exact
wpk ls --scope machine
wpk where 7zip --exact
wpk open 7zip --exact
wpk up 7zip --exact --dry-run
wpk update typora --dry-run     # 自动解析到完整包 ID，只预览
wpk update typora               # 交互终端中，来源未知时确认一次
wpk up Vendor.App --exact --via winget --dry-run
wpk ls --json
wpk find typora --verbose       # 显示完整实例 ID 和诊断
wpk find -- ls                 # 查询名恰好为命令名时，显式使用 find
```

搜索字段包括名称、原生包 ID、实例 ID，以及 Scoop manifest 中的命令别名。优先显示完整匹配、前缀和包含匹配；只有没有这些结果时才回退到子序列模糊匹配。`--exact` 仅接受完整名称或 ID，忽略大小写。多个模糊结果不会因为第一条得分更高而自动被选中。

操作选择时，精确实例 ID 优先，其次是唯一的精确名称、包 ID 或别名；部分匹配不会挤掉精确结果。例如已经识别到 Typora 的 `appmakes.Typora` 时，`wpk update typora` 会直接使用该包 ID，而不是将查询词传给 WinGet。

| 选项 | 说明 |
| --- | --- |
| `-h`, `--help` | 中文帮助；支持 `wpk up --help` |
| `--version` | 版本 |
| `--json` | 版本化 JSON；升级时同时禁用管理器交互 |
| `--verbose` | 列表显示完整实例 ID，详情显示原始证据，并展开完整诊断 |
| `--exact` | 精确匹配，适用于带查询的命令 |
| `--scope user\|machine` | 按安装范围筛选；不用于更改或猜测范围 |
| `--via scoop\|winget` | 显式选择已有识别关系的管理器，仅 `up` |
| `--dry-run` | 返回决策、程序路径及参数，不弹出确认或启动升级，仅 `up` 及其别名 |
| `--` | 后续内容全部作为查询文字 |

默认列表显示“软件、版本、范围、安装归属、管理器识别”，长实例 ID 放到详情和 `--verbose` 输出。窄终端自动改为逐项显示；源警告通常只给一条摘要，可通过 `doctor` 或 `--verbose` 查看详情。`NO_COLOR` 禁用颜色和动画；输出重定向时也不产生 ANSI 控制序列。

`where`、`open`、`up` 都要求明确的安装实例。交互终端中，多个结果会显示选择列表，包含版本、范围、归属和目录；第一项为“取消”。非交互环境或 dry-run 遇到多个候选时，返回歧义并列出完整 ID，例如 `wpk where registry:0123456789abcdef0123 --exact`。这是示例 ID，实际值由本机安装身份生成。合并前任意一条观察记录的 ID 也可用于精确选择。

## 来源、归属与去重

- **Scoop**：依据安装根目录中当前版本的安装元数据确认 Scoop 归属。支持用户及全局根目录、`SCOOP` / `SCOOP_GLOBAL`、Scoop 配置中的根路径及新旧 metadata 文件名。manifest 的历史版本目录不会重复列出。
- **WinGet**：通过微软官方 COM API，将本机安装记录与配置的仓库关联；保留精确包 ID、仓库、版本、范围、产品代码及目录。普通匹配只代表“识别”。不解析 CLI 表格、不读取 WinGet 内部数据库、不自动接受仓库协议或主动刷新仓库。
- **注册表**：只读扫描 HKCU/HKLM 的卸载记录及适用的 32/64 位视图。默认过滤 `SystemComponent=1` 和无显示名称的记录。视图单独保留，不推断软件架构。注册表记录本身不能证明原始安装渠道。

WinGet portable 安装标记只有在包 ID、仓库标识、安装范围和完整目录与当前 API 观察交叉验证后，才会被提升为已确认的 WinGet 归属。

合并优先使用产品代码、包身份和完整目录，检查范围、版本、发布者、架构和路径冲突。缺少强身份时，仅允许名称、发布者、版本、范围一致的双方唯一匹配。模糊搜索不参与合并。存在不兼容候选时保留独立实例，防止未知目录记录连接两个不同安装。

同名软件位于不同目录、范围或版本时会保留多行。每个合并实例保留 `records`、原始 `evidence` 和 `mergeReasons`。实例 ID 在其来源身份不变时保持一致；来源增加或安装身份变化可能改变主实例 ID，旧观察 ID 仍可用于定位存在的记录。

安装目录只使用明确记录的 Scoop 当前目录、WinGet 安装位置或注册表 `InstallLocation`；缺少信息就报告未知。不从 `UninstallString` 或 `msiexec.exe` 路径猜测目录。

## 升级规则

1. 唯一精确匹配且已确认归属的安装，显示目标及实际程序和参数，然后委托对应管理器执行，不重复确认。
2. 安装来源未知但有唯一、有效的 WinGet 升级目标时，交互终端先展示软件、完整包 ID、仓库、范围和来源状态，默认选择“不升级”。确认只授权本次操作，不改变历史来源标签。
3. 只有部分或模糊匹配时，升级前需要确认目标；从候选列表主动选择过的软件不重复确认其名称。来源未知时仍需要管理器确认。拒绝、取消、输入结束或 Ctrl+C 均不会启动尚未执行的升级。
4. `--json` 或输入、输出重定向时不弹出交互。来源未知的实际升级仍要求显式 `--via winget`；非精确匹配须改用完整名称或 ID，多候选须进一步限定。不提供 `--yes` 绕过这些要求。
5. `--dry-run` 不选择候选、不询问确认、不执行升级。唯一目标仅缺少确认时仍成功返回预览，并标记 `confirmationRequired=true`；结构冲突或目标不明确时继续返回错误。
6. 确认和 `--via` 都无法绕过多个仓库、归属冲突、未知范围或底层管理器不能区分多个实例的问题；已确认的 Scoop 安装不能交给 WinGet 接管。
7. 没有管理器候选的注册表记录只给出人工升级提示。
8. 用户确认之后、执行之前重新采集并比较实例身份、版本、目录、归属和升级候选；已知目录全部失效、记录变化或相关来源不完整时拒绝执行。某个无关仓库失败，不会阻止明确固定到另一个健康仓库的升级。

Scoop 通过 `PowerShell -NoProfile -File <scoop.ps1> update <package>` 执行，全局安装附带 `--global` 并设置对应 Scoop 根目录环境变量。WinGet 使用 `upgrade --id <id> --exact --source <repository> --scope <scope>`。程序及每个参数独立传递，不拼接查询文字到 shell 命令。WPK 不自动提权、不自动接受协议，也不执行从注册表读取的卸载命令。

人类输出会转发管理器 stdout/stderr；`--json` 将它们收集到结果对象中，WinGet 同时附带 `--disable-interactivity`。输入或输出被重定向时也禁用管理器交互。权限、协议或安装器条件不满足时由管理器报错，WPK 保留原始退出码。Ctrl+C 会尝试结束委托进程树；取消不表示安装程序已经回滚。

Scoop 的 held/pinned 包、WinGet 的版本比较和更新可用性均交给管理器处理。WPK 的“管理器执行完成”表示委托进程成功退出，不保证软件版本发生变化。

## JSON 与退出码

所有 `--json` 响应的标准输出都是一个 JSON 文档，错误也使用相同信封。字段和状态枚举使用英文；说明文字为中文。示例：

```json
{
  "schemaVersion": 1,
  "data": [],
  "diagnostics": [],
  "partial": false,
  "exitCode": 0
}
```

- `ls`、`find` 和默认查询的 `data` 均为实例数组，包含 `id`、名称、范围、`owners`、目录、原始记录及合并依据。
- `where` / `open` 成功返回 `instanceId`、`directory`、`opened`。
- `up` 返回 `decision`；允许时包含 `command.fileName`、独立的 `command.arguments` 和 `dryRun`，实际执行还包含 `process.exitCode`、`standardOutput`、`standardError`、超时及取消状态。
- 升级预览额外包含 `confirmationRequired`。其为 true 时，`decision.allowed=false`、`decision.code="confirmationRequired"`，同时仍提供目标及命令；退出码 0 表示预览成功，不代表已授权或执行。实际 JSON 升级需要显式 `--via` 等条件满足。
- `diagnostics` 包含 `source`、`code`、`message`、`severity` 及可选的 `repository`。
- 只读查询可以在 `partial=true` 时成功返回已采集结果；脚本需要完整清单时必须同时检查该字段。`doctor` 在发现来源不可用或环境问题时返回 5。

```powershell
$result = wpk ls --json | ConvertFrom-Json
if ($result.partial) { $result.diagnostics | Format-Table source, code, message }
$result.data | Select-Object id, name, version, scope
```

| 退出码 | 含义 |
| --- | --- |
| 0 | 成功；只读查询还需按需检查 `partial` |
| 1 | 没有匹配结果 |
| 2 | 参数错误 |
| 3 | 匹配结果不唯一 |
| 4 | 无法确定操作：来源未知、身份冲突、目录未知等 |
| 5 | 环境、目录或适配器不可用；doctor 发现问题 |
| 6 | 管理器执行失败；原始退出码保留于 `process.exitCode` |
| 130 | 操作取消 |

命令执行展示中的引号用于说明独立参数边界，不是供 shell 直接复制的转义脚本。脚本应读取 JSON 参数数组。

## 架构与扩展

```text
src/Wpk.Core   记录、证据、实例合并、搜索、升级决策、系统访问接口（仅 BCL）
src/Wpk.Cli    参数解析、中文/JSON 输出、Windows 来源和管理器适配器
tests/Wpk.Tests  xUnit 测试，使用临时安装目录、注册表快照和系统操作替身
```

`IInventorySource` 返回观察记录、诊断和完整性；`IUpgradeAdapter` 构造命令；`IProcessRunner` 管理独立参数、输出、超时与取消；`IDirectoryOpener` 负责打开目录。`ICliRenderer` 和 `IUserInteraction` 分别负责展示与选择/确认，可注入测试替身。添加新管理器时增加来源及升级适配器，复用现有实例、搜索和决策流程。

运行时依赖为微软官方 WinGet ComInterop、Spectre.Console 和 Spectre.Console.Cli。命令及帮助由 Spectre 管理；快捷查询只保留入口路由适配。依赖注入使用小型命令注册适配，不额外引入容器、数据库、常驻服务或持久清单缓存。NuGet 依赖通过各项目的 `packages.lock.json` 锁定。

## 验证与限制

自动化测试覆盖新旧 Scoop 元数据、损坏记录、活动版本、多来源去重与误合并、搜索和歧义、升级别名与完整包 ID、确认/拒绝/取消及再次验证、JSON、窄屏和中文、颜色降级、字面量进程参数、大量 stdout/stderr、超时和取消。界面测试使用 Spectre 测试控制台；升级及目录打开使用替身；进程测试只运行临时 PowerShell 辅助脚本。

Windows CI 执行锁定依赖还原、Release 测试、x64 发布和已发布程序的帮助检查，并上传 ZIP 与测试报告。真实软件升级、资源管理器打开和其他 Windows/WinGet 版本应在专门测试环境验收。

已知边界：

- 卸载注册表不覆盖所有安装方式；没有注册且不被 Scoop/WinGet 记录的便携程序不在首版清单内。
- WinGet API 依赖本机 App Installer。旧版 API、网络或权限问题会产生明确诊断，不自动修复。整个 WinGet 采集有 25 秒上限。
- 对要求调用方接受协议的仓库（通常包括 `msstore`），首版返回 `sourceAgreementRequired` 并跳过。WinGet CLI 的已有确认不等于对 WPK 的确认；首版没有接受仓库协议的入口。其余仓库及 Scoop、注册表清单仍可使用。
- WinGet 映射和元数据沿用管理器的判断；无法确定范围或唯一性的目标会被拒绝升级。历史渠道未知时，可通过交互确认或显式指定 `--via winget` 授权本次操作。
- Scoop `NO_JUNCTION` 模式下，仅有一个版本目录时可识别；有多个版本且没有 `current` 时不猜测活动版本，返回诊断。
- 各来源独立采集，升级前再验证可缩小状态变化窗口，但无法与外部管理器建立跨进程安装事务。
- 安装、卸载、批量升级、配置化别名、常驻缓存及自包含/NativeAOT 发布不在首版范围。

参考：[WinGet 的 list 行为](https://learn.microsoft.com/en-us/windows/package-manager/winget/list)、[WinGet COM C# 示例](https://github.com/microsoft/winget-cli/tree/master/samples/MinimalCallers)、[Scoop 元数据实现](https://github.com/ScoopInstaller/Scoop/blob/master/lib/manifest.ps1)。
