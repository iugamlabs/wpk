[English](../README.md) | [简体中文](../README.zh-CN.md) · [使用参考](reference.md) · [设计说明](design.md) · [开发指南](development.md)

# 开发指南

以下命令均在仓库根目录执行。

## 构建与运行

开发需要 Windows x64、.NET 10 SDK，以及首次还原依赖时的 NuGet 网络连接。发布目录运行需要 .NET 10 Runtime。WinGet 和 Scoop 都是可选来源：缺少其中一个时，其他来源继续工作。

```powershell
dotnet restore Wpk.slnx --locked-mode
dotnet build Wpk.slnx --no-restore
dotnet test Wpk.slnx --no-restore

dotnet run --project src/Wpk.Cli -- --help
dotnet run --project src/Wpk.Cli -- doctor
dotnet run --project src/Wpk.Cli -- find git
```

本地可按三种模式发布 **single-file** `wpk.exe`（正式发版与此一致）。`PublishSingleFile` / 自包含需要额外还原 win-x64 runtime pack，因此 publish 不要加 `--no-restore`。`PublishTrimmed` 仅自包含可用（框架依赖会报 `NETSDK1102`）：

```powershell
# 框架依赖（需要本机安装 .NET 10 Runtime；不能 IL 裁剪，体积主要来自 Windows.SDK.NET 投影）
dotnet publish src/Wpk.Cli -c Release -r win-x64 --self-contained false -o artifacts/wpk-fd `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -p:DebugSymbols=false

# 自包含 + IL 裁剪 + 单文件压缩（正式 -sc）
dotnet publish src/Wpk.Cli -c Release -r win-x64 --self-contained true -o artifacts/wpk-sc `
  -p:PublishSingleFile=true -p:PublishTrimmed=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false

# 自包含 + 裁剪 + ReadyToRun（启动更快，体积大于 -sc）
dotnet publish src/Wpk.Cli -c Release -r win-x64 --self-contained true -o artifacts/wpk-r2r `
  -p:PublishSingleFile=true -p:PublishTrimmed=true -p:PublishReadyToRun=true `
  -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=none -p:DebugSymbols=false

.\artifacts\wpk-fd\wpk.exe --help
```

发布目录里可能还有 README / docs；日常使用只需把对应的 `wpk.exe` 放到 PATH 中的目录。框架依赖版仍需要本机 .NET 10 Runtime。不需要安装后台服务。

Native AOT 目前不可用：Spectre.Console.Cli 依赖反射。`--json` 已改用 `System.Text.Json` source generation，便于裁剪发布。

项目最低 Windows API 版本为 10.0.19041；框架依赖运行仍需满足 .NET 10 的支持要求。当前发布、CI 和测试目标为 `win-x64`，其他架构尚未验收。

## 测试与验收

自动化测试覆盖新旧 Scoop 元数据、损坏记录、活动版本、多来源去重与误合并、搜索和歧义、升级别名与完整包 ID、确认/拒绝/取消及再次验证、JSON、窄屏和中文、颜色降级、字面量进程参数、大量 stdout/stderr、超时和取消。界面测试使用 Spectre 测试控制台；升级及目录打开使用替身；进程测试只运行临时 PowerShell 辅助脚本。

## CI 与发布

Windows CI（`build-and-test`）在分支 push、PR 和手动触发时运行：锁定依赖还原、Release 测试、x64 框架依赖 single-file 发布、已发布程序的帮助检查，并上传版本化 `.exe` 与测试结果。测试结果由 `test-report` 工作流通过 `dorny/test-reporter` 写成 Check。Dependabot 每周检查 GitHub Actions 与 NuGet 更新。

程序版本由 **git tag** 决定（[MinVer](https://github.com/adamralph/minver)，前缀 `v`）。`Directory.Build.props` 只保留共享编译选项，不再手写版本号。未打 tag 的提交会得到带 `alpha` 高度后缀的开发版本（例如 `0.0.0-alpha.0.12`）；打在提交上的 `v1.0.0` 会使 `wpk --version` 输出 `1.0.0`。

正式发版只接受符合 `vX.Y.Z` 的 tag。推送该 tag 会运行 `release` 工作流：跑测试，分别发布框架依赖 / 自包含（IL 裁剪）/ ReadyToRun（IL 裁剪）三种 single-file 可执行文件，校验各自 `--version` 与 tag 一致，生成：

- `wpk-{version}-win-x64.exe`（框架依赖；不能 `PublishTrimmed`）
- `wpk-{version}-win-x64-sc.exe`（自包含 + 裁剪 + 单文件压缩）
- `wpk-{version}-win-x64-r2r.exe`（自包含 + 裁剪 + ReadyToRun）
- `SHA256SUMS.txt`

并创建 GitHub Release。

```powershell
git tag v1.0.0
git push origin v1.0.0
```

真实软件升级、资源管理器打开和其他 Windows/WinGet 版本应在专门测试环境验收。
