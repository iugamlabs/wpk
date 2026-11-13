[English](README.md) | [简体中文](README.zh-CN.md)

# WPK — Windows Package Kit

A lightweight Windows CLI to find, locate, and upgrade installed apps. WPK brings together installation records from Scoop, WinGet, and the Windows registry.

## Features

- Search installed apps by name or ID and view their details.
- Find installation folders or open them in File Explorer.
- Upgrade apps through Scoop or WinGet, with a preview before you run.
- Export inventory as JSON and diagnose unavailable sources.

## Requirements

- Windows x64 with .NET 10 Runtime; other architectures have not been validated.
- Scoop and WinGet are optional. Available sources continue to work if either is missing.
- CLI help and human-readable output are currently in Chinese.

## Installation

Download a **win-x64 single-file** build from [GitHub Releases](https://github.com/iugamlabs/wpk/releases):

| Asset | Mode | Notes |
| --- | --- | --- |
| `wpk-{version}-win-x64.exe` | Framework-dependent | Needs [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0); smallest download |
| `wpk-{version}-win-x64-sc.exe` | Self-contained | Bundles the runtime; no separate .NET install |
| `wpk-{version}-win-x64-r2r.exe` | Self-contained + ReadyToRun | Faster startup; larger than `-sc` |

Rename the file to `wpk.exe`, place it on your `PATH`, and verify with `wpk --version`. Checksums are in `SHA256SUMS.txt`.

## Usage

| Command | Description |
| --- | --- |
| `wpk <name>` | Show app details or matching candidates |
| `wpk find <name>` | Search installed apps |
| `wpk ls` | List installed apps |
| `wpk where <name>` | Print the installation folder |
| `wpk open <name>` | Open the installation folder in File Explorer |
| `wpk up <name>` | Upgrade with the appropriate manager; aliases: `update`, `upgrade` |
| `wpk doctor` | Check the environment and inventory sources |

```powershell
wpk find git
wpk where 7zip --exact
wpk up typora --dry-run
wpk ls --json
wpk --help
```

Use `--exact` to match a full name or ID, `--scope user|machine` to filter by installation scope, and `--verbose` for IDs and diagnostics. See the [usage reference](docs/reference.md) for all options.

## Before upgrading

- WPK searches **installed apps**, not remote package catalogs. Installation, uninstallation, and bulk upgrades are not supported.
- **WinGet recognition does not prove that WinGet installed an app.** WPK keeps recognition and confirmed ownership separate. Unknown ownership stays unknown.
- Use `--dry-run` to preview the target and command without upgrading. WPK may ask you to select an installation or confirm the manager; ambiguous or conflicting targets are refused.
- Apps with no installation record may be missing from the inventory, and some installation folders may be unknown. Run `wpk doctor` to investigate source issues.

## Documentation

Detailed documentation is currently in Chinese:

- [Usage reference](docs/reference.md) — options, matching, upgrade rules, JSON, exit codes, and limitations.
- [Design](docs/design.md) — sources, ownership, deduplication, and architecture.
- [Development](docs/development.md) — build, test, publish, and verification.
