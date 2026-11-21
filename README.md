<div align="center">

<img src="assets/app.png" alt="WorkspaceRestorer" width="96">

# WorkspaceRestorer

A portable desktop workspace tool for Windows 10/11 x64. It can save and restore work sessions and desktop icon positions across different monitor layouts, manage startup application plans, and run locally.

**English** · [简体中文](README.zh-CN.md)

[![Release](https://img.shields.io/github/v/release/lix996/WorkspaceRestorer?style=flat-square&label=release)](https://github.com/lix996/WorkspaceRestorer/releases/latest)
[![License](https://img.shields.io/github/license/lix996/WorkspaceRestorer?style=flat-square)](LICENSE)

[Download](#download) · [Features](#features) · [Usage](#usage) · [Security](#security) · [Build from source](#build-from-source) · [License](#license)

</div>

## Download

Get the latest version from [GitHub Releases](https://github.com/lix996/WorkspaceRestorer/releases/latest).

| OS | Type | Download |
| --- | --- | --- |
| Windows 10/11 x64 | Portable | [WorkspaceRestorer-v1.0.0-windows-x64.zip](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.0/WorkspaceRestorer-v1.0.0-windows-x64.zip) |
| Windows 10/11 x64 | Portable | [WorkspaceRestorer-v1.0.0-windows-x64-zh.zip](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.0/WorkspaceRestorer-v1.0.0-windows-x64-zh.zip) |

> Portable. Extract and run `WorkspaceRestorer.exe`. The package without `-zh` uses English data directory names; the `-zh` package uses Chinese data directory names.

## Features

- **Startup & Restore**: Save the currently open apps, folders, browser pages, and VS Code windows as a work plan, then restore them in order.
- **Startup Apps**: Scan desktop items, select entries to add to a startup plan, and launch them at login or on demand.
- **Desktop Icon Layout**: Save desktop icon positions per monitor layout, and drag icons or handle new icons in the editor.
- **Local runtime**: No installation or account required; the app runs locally and stores data in the `data` directory next to the executable.

## Usage

1. Download and extract the matching zip.
2. Run `WorkspaceRestorer.exe`; the first launch creates a `data` directory next to it.
3. To migrate, keep the `data` directory.

## Security

The current build is unsigned. Windows SmartScreen may appear on first launch.

### Verify the download

Compare the SHA-256 with the checksum on the [release page](https://github.com/lix996/WorkspaceRestorer/releases/tag/v1.0.0).

```powershell
Get-FileHash .\WorkspaceRestorer-v1.0.0-windows-x64.zip -Algorithm SHA256
Get-FileHash .\WorkspaceRestorer-v1.0.0-windows-x64-zh.zip -Algorithm SHA256
```

### Windows SmartScreen

If **Windows protected your PC** appears:

1. Click **More info**.
2. Click **Run anyway**.

The exact button labels may vary by Windows version.

If you downloaded the zip, you can also try: right-click the zip → **Properties** → **Unblock** before extracting.

### VirusTotal

[Scan report from 2026-11-21](https://www.virustotal.com/gui/file/B3864FFD1FEF8CBD459C9896CDF7CD5A29AF634A36FAEB55DD5C1184321D52F1).

The result is shown on the report page and may change as engines update.

### Feedback

If you encounter a false positive or another issue, open an [Issue](https://github.com/lix996/WorkspaceRestorer/issues).

## Build from source

Requirements:

- Windows 10/11 x64
- .NET Framework 4.8
- The system compiler at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

Clone the repository:

```powershell
git clone https://github.com/lix996/WorkspaceRestorer.git
cd WorkspaceRestorer
```

From the repository root:

```powershell
# English fixed directory names (default en-US)
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1

# Chinese fixed directory names (on demand)
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Variant zh-CN
```

Outputs:

- `zuizhongchanwu\WorkspaceRestorer.exe` (en-US)
- `zuizhongchanwu-zh\WorkspaceRestorer.exe` (zh-CN)

In a GitHub clone these folders are at the repository root. In the local outer project they are one level above `yuanwenjian`, next to the real data.

Without `-Variant`, only en-US is built. Chinese requires `-Variant zh-CN`; to build both, run the script twice.

## Tests

From the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test.ps1
```

Chinese variant:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test.ps1 -Variant zh-CN
```

The script builds the app first, then runs the regression suite against a temporary data directory. It never writes real user data.

## Repository layout

```text
product.json
src\WorkspaceRestorer\   Product source (C# 5, pure-code WPF, no XAML)
scripts\                 Build.ps1, Test.ps1, identity guard, note templates
tests\                   Regression tests
variants\                Fixed data-folder names (en-US / zh-CN)
assets\                  app.ico, app.png
```

Directory note files (`说明.txt`) are not published. See `src\WorkspaceRestorer\代码结构说明.md` for the source tree.

## Data and privacy

Runtime data lives in `data\default` next to the exe. This repository contains no user data, logs, backups, or screenshots. Tests use temporary data directories only.

## Support

If this tool is useful to you, a star is appreciated. For bugs and feature requests, use [Issues](https://github.com/lix996/WorkspaceRestorer/issues).

## License

GPL-3.0. See [LICENSE](LICENSE).
