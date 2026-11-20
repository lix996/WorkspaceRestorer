# WorkspaceRestorer

A local Windows 10/11 desktop workspace tool: save and restore work sessions, manage startup apps, preserve desktop icon layouts per monitor topology, and keep a shared recycle bin. It does not depend on any cloud service.

[English](README.md) | [简体中文](README.zh-CN.md)

## Features

Four workspaces:

1. **Startup & Restore**: save the current apps, folders, browser pages, and VS Code windows as a work plan, then restore them in order.
2. **Startup Apps**: scan desktop entries, add selected items to startup plans, and launch them at login or on demand.
3. **Desktop Icon Layout**: save desktop icon positions per monitor topology, and drag icons or handle new icons in the editor.
4. **Recycle Bin**: a shared deletion area for the three workspaces; restore or permanently delete items.

## Build from source

Requirements:

- Windows 10/11 x64
- .NET Framework 4.8
- The system compiler at `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

From the repository root:

```powershell
# English fixed folder names (default en-US)
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1

# Chinese fixed folder names (on demand)
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
assets\                  app.ico
```

Directory note files (`说明.txt`) are not published. See `src\WorkspaceRestorer\代码结构说明.md` for the source tree.

## Data and privacy

Runtime data lives in `data\default` next to the exe. This repository contains no user data, logs, backups, or screenshots. Tests use temporary data directories only.

## License

MIT. See [LICENSE](LICENSE).
