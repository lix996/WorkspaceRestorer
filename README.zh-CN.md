<div align="center">

<img src="assets/app.png" alt="WorkspaceRestorer" width="96">

# WorkspaceRestorer

适用于 Windows 10/11 x64 的免安装电脑桌面工作区工具，可保存和恢复工作会话、管理开机自启动方案、按显示器布局保存和恢复桌面图标位置，程序在本地运行。

[English](README.md) · **简体中文**

[![Release](https://img.shields.io/github/v/release/lix996/WorkspaceRestorer?style=flat-square&label=release)](https://github.com/lix996/WorkspaceRestorer/releases/latest)
[![License](https://img.shields.io/github/license/lix996/WorkspaceRestorer?style=flat-square)](LICENSE)

[下载](#下载) · [功能](#功能) · [使用](#使用) · [从源码构建](#从源码构建) · [许可证](#许可证)

</div>

## 下载

从 [GitHub Releases](https://github.com/lix996/WorkspaceRestorer/releases/latest) 获取最新版本。

| 系统 | 类型 | 下载 |
| --- | --- | --- |
| Windows 10/11 x64 | 免安装版 | [WorkspaceRestorer-v1.0.0-windows-x64.zip](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.0/WorkspaceRestorer-v1.0.0-windows-x64.zip) |
| Windows 10/11 x64 | 免安装版 | [WorkspaceRestorer-v1.0.0-windows-x64-zh.zip](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.0/WorkspaceRestorer-v1.0.0-windows-x64-zh.zip) |

> 免安装版，解压后运行 `WorkspaceRestorer.exe`。不带 `-zh` 的包使用英文数据目录名，带 `-zh` 的包使用中文数据目录名。

## 功能

- **启动与恢复**：把当前打开的应用、文件夹、浏览器网页和 VS Code 窗口保存成“工作方案”，之后按顺序恢复。
- **自启动应用**：扫描桌面项目，勾选后加入“自启动方案”，登录或打开程序时按方案启动。
- **图标位置保存**：按显示器组合保存桌面图标位置，也可在编辑页里拖动图标、处理新图标。
- **本地运行**：免安装、无账号，程序在本地运行；数据保存在 exe 旁边的 `data` 目录。

## 使用

1. 下载并解压对应 zip。
2. 运行 `WorkspaceRestorer.exe`；首次运行会在同目录创建 `data` 数据目录。
3. 需要迁移时，保留 `data` 目录即可。

## 从源码构建

环境要求：

- Windows 10/11 x64
- .NET Framework 4.8
- 系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

克隆仓库：

```powershell
git clone https://github.com/lix996/WorkspaceRestorer.git
cd WorkspaceRestorer
```

在仓库根目录（本地外层项目里是 `yuanwenjian\`）运行：

```powershell
# 英文固定目录名（默认变体 en-US）
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1

# 中文固定目录名（按需构建）
powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Variant zh-CN
```

输出：

- `zuizhongchanwu\WorkspaceRestorer.exe`（en-US）
- `zuizhongchanwu-zh\WorkspaceRestorer.exe`（zh-CN）

GitHub 克隆里这两个目录在仓库根；本地外层项目里它们在 `yuanwenjian` 的上一级，和真实数据放在一起。

不传 `-Variant` 时只构建 en-US；中文版必须显式加 `-Variant zh-CN`；两个都要就分两次构建。

## 测试

在仓库根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test.ps1
```

中文目录名变体：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test.ps1 -Variant zh-CN
```

测试会先用 `Build.ps1` 构建产品，再把测试数据放到系统临时目录；不会写真实用户数据。

## 仓库结构

```text
product.json
src\WorkspaceRestorer\   产品源码（C# 5，纯代码 WPF，无 XAML）
scripts\                 Build.ps1、Test.ps1、身份检查、说明模板
tests\                   回归测试
variants\                两套固定数据目录名（en-US / zh-CN）
assets\                  app.ico、app.png
```

目录说明文件 `说明.txt` 不随仓库发布；源码目录结构见 `src\WorkspaceRestorer\代码结构说明.md`。

## 数据与隐私

程序运行数据写在 exe 旁边的 `data\default`；仓库不包含用户数据、日志、备份或截图。测试只使用临时数据目录。

## 支持

如果这个工具对你有帮助，欢迎点一个 Star；问题和建议请提交到 [Issues](https://github.com/lix996/WorkspaceRestorer/issues)。

## 许可证

MIT，见 [LICENSE](LICENSE)。
