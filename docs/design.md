[English](../README.md) | [简体中文](../README.zh-CN.md) · [使用参考](reference.md) · [设计说明](design.md) · [开发指南](development.md)

# 设计说明

WPK 分别保留观察来源、管理器识别关系和安装归属；无法证明的归属保持未知。WinGet 能识别某个软件，不代表它最初由 WinGet 安装。

## 来源、归属与去重

- **Scoop**：依据安装根目录中当前版本的安装元数据确认 Scoop 归属。支持用户及全局根目录、`SCOOP` / `SCOOP_GLOBAL`、Scoop 配置中的根路径及新旧 metadata 文件名。manifest 的历史版本目录不会重复列出。
- **WinGet**：通过微软官方 COM API，将本机安装记录与配置的仓库关联；保留精确包 ID、仓库、版本、范围、产品代码及目录。普通匹配只代表“识别”。不解析 CLI 表格、不读取 WinGet 内部数据库、不自动接受仓库协议或主动刷新仓库。
- **注册表**：只读扫描 HKCU/HKLM 的卸载记录及适用的 32/64 位视图。默认过滤 `SystemComponent=1` 和无显示名称的记录。视图单独保留，不推断软件架构。注册表记录本身不能证明原始安装渠道。

WinGet portable 安装标记只有在包 ID、仓库标识、安装范围和完整目录与当前 API 观察交叉验证后，才会被提升为已确认的 WinGet 归属。

合并优先使用产品代码、包身份和完整目录，检查范围、版本、发布者、架构和路径冲突。缺少强身份时，仅允许名称、发布者、版本、范围一致的双方唯一匹配。模糊搜索不参与合并。存在不兼容候选时保留独立实例，防止未知目录记录连接两个不同安装。

同名软件位于不同目录、范围或版本时会保留多行。每个合并实例保留 `records`、原始 `evidence` 和 `mergeReasons`。实例 ID 在其来源身份不变时保持一致；来源增加或安装身份变化可能改变主实例 ID，旧观察 ID 仍可用于定位存在的记录。

安装目录只使用明确记录的 Scoop 当前目录、WinGet 安装位置或注册表 `InstallLocation`；缺少信息就报告未知。不从 `UninstallString` 或 `msiexec.exe` 路径猜测目录。

## 架构与扩展

```text
src/Wpk.Core   记录、证据、实例合并、搜索、升级决策、系统访问接口（仅 BCL）
src/Wpk.Cli    参数解析、中文/JSON 输出、Windows 来源和管理器适配器
tests/Wpk.Tests  xUnit 测试，使用临时安装目录、注册表快照和系统操作替身
```

`IInventorySource` 返回观察记录、诊断和完整性；`IUpgradeAdapter` 构造命令；`IProcessRunner` 管理独立参数、输出、超时与取消；`IDirectoryOpener` 负责打开目录。`ICliRenderer` 和 `IUserInteraction` 分别负责展示与选择/确认，可注入测试替身。添加新管理器时增加来源及升级适配器，复用现有实例、搜索和决策流程。

运行时依赖为微软官方 WinGet ComInterop、Spectre.Console 和 Spectre.Console.Cli。命令及帮助由 Spectre 管理；快捷查询只保留入口路由适配。依赖注入使用小型命令注册适配，不额外引入容器、数据库、常驻服务或持久清单缓存。NuGet 依赖通过各项目的 `packages.lock.json` 锁定。

## 参考资料

参考：[WinGet 的 list 行为](https://learn.microsoft.com/en-us/windows/package-manager/winget/list)、[WinGet COM C# 示例](https://github.com/microsoft/winget-cli/tree/master/samples/MinimalCallers)、[Scoop 元数据实现](https://github.com/ScoopInstaller/Scoop/blob/master/lib/manifest.ps1)。
