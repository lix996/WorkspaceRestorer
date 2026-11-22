<div align="center">

<img src="assets/app.png" alt="WorkspaceRestorer" width="96">

# WorkspaceRestorer

适用于 Windows 10/11 x64 的桌面工作区工具，可保存和恢复工作会话、按显示器布局保存和恢复桌面图标位置、管理开机自启动方案，也能处理从外部复制进来的方案文件夹。可以安装使用，也可以直接用免安装版；程序在本地运行。

[English](README.md) · **简体中文**

[![Release](https://img.shields.io/github/v/release/lix996/WorkspaceRestorer?style=flat-square&label=release)](https://github.com/lix996/WorkspaceRestorer/releases/latest)
[![License](https://img.shields.io/github/license/lix996/WorkspaceRestorer?style=flat-square)](LICENSE)

[下载](#下载) · [功能](#功能) · [使用](#使用) · [安全验证](#安全验证) · [从源码构建](#从源码构建) · [许可证](#许可证)

</div>

## 下载

从 [GitHub Releases](https://github.com/lix996/WorkspaceRestorer/releases/latest) 获取最新版本。

| 系统 | 类型 | 下载 |
| --- | --- | --- |
| Windows 10/11 x64 | 安装版（英文目录名） | [WorkspaceRestorer-Setup-v1.0.1-windows-x64.exe](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.1/WorkspaceRestorer-Setup-v1.0.1-windows-x64.exe) |
| Windows 10/11 x64 | 安装版（中文目录名） | [WorkspaceRestorer-Setup-v1.0.1-windows-x64-zh.exe](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.1/WorkspaceRestorer-Setup-v1.0.1-windows-x64-zh.exe) |
| Windows 10/11 x64 | 免安装版（英文目录名） | [WorkspaceRestorer-v1.0.1-windows-x64.zip](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.1/WorkspaceRestorer-v1.0.1-windows-x64.zip) |
| Windows 10/11 x64 | 免安装版（中文目录名） | [WorkspaceRestorer-v1.0.1-windows-x64-zh.zip](https://github.com/lix996/WorkspaceRestorer/releases/download/v1.0.1/WorkspaceRestorer-v1.0.1-windows-x64-zh.zip) |

> **安装版**：按当前用户安装，不请求管理员权限，也不弹 UAC。默认安装到 `%LOCALAPPDATA%\Programs\WorkspaceRestorer`，也可以选择任何当前用户有写入权限的目录。覆盖安装到同一目录会保留已有的 `data` 目录；卸载时也会保留 `data`。
>
> **免安装版**：解压后运行 `WorkspaceRestorer.exe`。数据保存在 exe 旁边的 `data` 目录。不带 `-zh` 的包使用英文数据目录名，带 `-zh` 的包使用中文数据目录名。每个包还包含 `LICENSE` 和 `SOURCE.txt`。

## 功能

- **启动与恢复**：把当前打开的应用、文件夹、浏览器网页和 VS Code 窗口保存成“工作方案”，之后按顺序恢复。
- **自启动应用**：扫描桌面项目，勾选后加入“自启动方案”，登录或打开程序时按方案启动。
- **图标位置保存**：按显示器组合保存桌面图标位置，也可在编辑页里拖动图标、处理新图标。
- **未登记方案收编**：`workflows` 下出现清单里没有的方案文件夹时，可以选择 **导入**、**暂不导入** 或 **移到“无法识别”**。暂不导入只跳过本次登录；移动只搬走、不删除；读不出来的文件夹会移到 `Backup\Unrecognized`，以后还能找回。
- **本地运行**：无账号；可以用安装版或免安装版，数据都保存在 exe 旁边的 `data` 目录。

## 使用

1. 下载安装版或免安装版，并选择需要的数据目录名（英文或中文）。
2. 安装版：运行 setup，选择有写入权限的目录。免安装版：解压 zip，运行 `WorkspaceRestorer.exe`。
3. 首次运行会在同目录创建 `data` 数据目录。
4. 升级免安装版时，替换 `WorkspaceRestorer.exe` 并保留 `data`；安装版覆盖安装同一目录时会保留 `data`。

## 安全验证

当前安装包和程序都未签名。首次运行 setup 或启动程序时，可能出现 Windows SmartScreen 提示。

### 校验下载

用 SHA-256 与 [v1.0.1 Release 页面](https://github.com/lix996/WorkspaceRestorer/releases/tag/v1.0.1)上的校验和比对。

```powershell
Get-FileHash .\WorkspaceRestorer-Setup-v1.0.1-windows-x64.exe -Algorithm SHA256
Get-FileHash .\WorkspaceRestorer-Setup-v1.0.1-windows-x64-zh.exe -Algorithm SHA256
Get-FileHash .\WorkspaceRestorer-v1.0.1-windows-x64.zip -Algorithm SHA256
Get-FileHash .\WorkspaceRestorer-v1.0.1-windows-x64-zh.zip -Algorithm SHA256
```

### Windows SmartScreen

如果出现“Windows 已保护你的电脑”：

1. 点“更多信息”
2. 点“仍要运行”

具体按钮以系统显示为准。

如果下载的是 setup 或 zip，也可以尝试：右键文件 → **属性** → 勾选“**解除锁定**”，再运行或解压。

### VirusTotal

可以把下载到的文件上传到 [VirusTotal](https://www.virustotal.com/) 做独立检查。结果显示在报告页，可能随引擎更新变化。

### 反馈

如果遇到误报或其他问题，请提交 [Issue](https://github.com/lix996/WorkspaceRestorer/issues)。

## 从源码构建

环境要求：

- Windows 10/11 x64
- .NET Framework 4.8
- 系统自带的 `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`
- 只有构建安装包时才需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)

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

两个 exe 构建好后，再构建两个安装包：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Package-Installer.ps1 -Variant all
```

setup 输出到 `build\installer`。安装版按当前用户安装，会显示目录选择页，默认 `%LOCALAPPDATA%\Programs\WorkspaceRestorer`，不会请求管理员权限。

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
scripts\                 Build.ps1、Test.ps1、安装包打包、身份检查、说明模板
scripts\installer\       Inno Setup 脚本、中文语言文件、目录说明
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

GPL-3.0，见 [LICENSE](LICENSE)。
