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

生成框架依赖的 x64 发布目录：

```powershell
dotnet publish src/Wpk.Cli -c Release -r win-x64 --self-contained false --no-restore -o artifacts/wpk-win-x64
.\artifacts\wpk-win-x64\wpk.exe --help
```

将**完整发布目录**放到固定位置，再把目录加入用户 PATH。保留其中的 DLL、WinMD、JSON 等文件；不能只复制 `wpk.exe`。不需要安装后台服务。

项目最低 Windows API 版本为 10.0.19041；运行系统仍需满足 .NET 10 的支持要求。当前发布、CI 和测试目标为 `win-x64`，其他架构尚未验收。

## 测试与验收

自动化测试覆盖新旧 Scoop 元数据、损坏记录、活动版本、多来源去重与误合并、搜索和歧义、升级别名与完整包 ID、确认/拒绝/取消及再次验证、JSON、窄屏和中文、颜色降级、字面量进程参数、大量 stdout/stderr、超时和取消。界面测试使用 Spectre 测试控制台；升级及目录打开使用替身；进程测试只运行临时 PowerShell 辅助脚本。

## CI 与发布

Windows CI（`build-and-test`）在分支 push、PR 和手动触发时运行：锁定依赖还原、Release 测试、x64 发布、已发布程序的帮助检查，并上传版本化 ZIP 与测试结果。测试结果由 `test-report` 工作流通过 `dorny/test-reporter` 写成 Check。Dependabot 每周检查 GitHub Actions 与 NuGet 更新。

正式发版只接受符合 `vX.Y.Z` 的 tag（例如 `v1.0.0`）。推送该 tag 会运行 `release` 工作流：用 tag 中的版本号覆盖程序集版本，跑测试与发布，校验 `wpk --version`，生成 `wpk-{version}-win-x64.zip` 与同名 `.sha256`，并创建 GitHub Release。日常开发版本仍以 `Directory.Build.props` 中的 `<Version>` 为准，发版前建议将其与即将打的 tag 对齐。

```powershell
# 发版示例：先把 Directory.Build.props 的 Version 改成 1.0.0，再打 tag
git tag v1.0.0
git push origin v1.0.0
```

真实软件升级、资源管理器打开和其他 Windows/WinGet 版本应在专门测试环境验收。
