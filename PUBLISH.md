# 发布到 GitHub

- 仓库：https://github.com/lix996/WorkspaceRestorer
- 可见性：公开
- 默认分支：main
- 许可证：GPL-3.0
- 仓库根：本目录（本地外层项目里是 `yuanwenjian\`）

## 发布范围

进仓库：

- `product.json`
- `src\**`
- `tests\**`
- `variants\**`
- `assets\**`
- `scripts\Build.ps1`
- `scripts\Test.ps1`
- `scripts\Test-IdentityConsistency.ps1`
- `scripts\Package-Installer.ps1`
- `scripts\installer\**`
- `scripts\说明模板\**`
- `README.md`、`README.zh-CN.md`、`PUBLISH.md`、`LICENSE`、`.gitignore`、`.gitattributes`

> `Package-Installer.ps1` 和 `scripts\installer\**` 仍保留在源码里，用于本地生成干净的 stage 或按需构建安装包；当前 Release 不发布 setup exe，只发布两个免安装 zip。

不发布：

- 所有 `说明.txt`（目录说明；`scripts\说明模板` 里的 3 个模板除外，构建要用）
- `build\`、`selfcheck-shots\`
- 186 个 `SelfCheck-*.ps1` 自检探针
- 本目录上一级的 `zuizhongchanwu\`、`zuizhongchanwu-zh\`（exe、真实 data、日志）
- 本目录上一级的 `验证截图\`、`memory-bank\`、`接续\`、`项目文档\`、`.workbuddy-ai\`
- 外层项目的设计稿、pptx、工具残留

## 首次上传（留档）

在本目录（仓库根）运行：

```powershell
git init -b main
git add -A
git commit -m "Initial open-source release: WorkspaceRestorer"
git remote add origin https://github.com/lix996/WorkspaceRestorer.git
git push -u origin main
```

这次从旧根目录结构改成“本目录就是仓库根”的迁移已经完成；下面这段只留档，不要再次执行 `--force`。

旧仓库备份在外层项目根：`.git-root-20261121`、`.git-old-20260918`，都不发布。

## 日常更新

在本目录运行：

```powershell
git status --short
git add -A
git commit -m "说明这次改了什么"
git push
```

## 发布新版本

以 `v1.0.1` 为例。旧版本 `v1.0.0` 的 tag、Release 和资产全部保留，不删、不改、不移动。

1. 修改 `src\WorkspaceRestorer\Shared\Infrastructure\AppInfo.cs` 的版本号，三个程序集版本和 `public const string Version` 必须一致。
2. 构建英文：`powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1`
3. 构建中文目录名版：`powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Variant zh-CN`
4. 两个变体都跑测试：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\Test.ps1
   powershell -ExecutionPolicy Bypass -File .\scripts\Test.ps1 -Variant zh-CN
   ```

5. 更新 `README.md`、`README.zh-CN.md`、本文件里的版本号和下载链接，然后提交并推送：

   ```powershell
   git add -A
   git commit -m "Bump version to 1.0.1"
   git push
   ```

   先提交再打包，zip 里的 `SOURCE.txt` 才会指向干净的源码提交，而不是带未提交改动的构建。

6. 生成干净的 stage：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\scripts\Package-Installer.ps1 -Variant all
   ```

   stage 输出到：

   ```text
   build\installer\stage\en-US\
   build\installer\stage\zh-CN\
   ```

   这个命令也会生成 setup，但 setup 只用于本地验证，不上传；当前 Release 只发两个 zip。

7. 用干净的 stage 打包两个免安装 zip，不要从 `zuizhongchanwu` 直接压缩（那里有真实 `data`）：

   ```text
   build\installer\stage\en-US\  →  WorkspaceRestorer-v1.0.1\
   build\installer\stage\zh-CN\  →  WorkspaceRestorer-v1.0.1-zh\
   ```

   每个 zip 内层结构：

   ```text
   WorkspaceRestorer-v1.0.1\
     WorkspaceRestorer.exe
     data\app.ico
     LICENSE
     SOURCE.txt

   WorkspaceRestorer-v1.0.1-zh\
     WorkspaceRestorer.exe
     data\app.ico
     LICENSE
     SOURCE.txt
   ```

   生成：

   ```text
   WorkspaceRestorer-v1.0.1-windows-x64.zip
   WorkspaceRestorer-v1.0.1-windows-x64-zh.zip
   ```

8. 检查两个资产：

   - 两个 zip 都能正常打开；
   - zip 内没有 `data\default`、设置、日志、备份或用户方案；
   - 两个文件都没有本地下载标记；
   - 记录字节数和 SHA-256。

9. 新建 tag `v1.0.1`，新建 Release `WorkspaceRestorer v1.0.1`，上传两个 zip：

   ```text
   WorkspaceRestorer-v1.0.1-windows-x64.zip
   WorkspaceRestorer-v1.0.1-windows-x64-zh.zip
   ```

10. Release 正文包含：下载表、1.0.1 变更、免安装版说明、系统要求、两个 zip 的 SHA-256、未签名说明、源码和许可证。旧 `v1.0.0` Release 不动。

11. 发布后下载两个 zip 复算 SHA-256，再更新 `项目文档\安全验证与发布检查.md`。

### 重新发布同一版本

只有同一个版本号的资产需要替换时才这样做：

- 不要删、不要移动已有 tag；
- 编辑已有 Release，不要新建另一个同名 tag；
- GitHub 不允许同名 asset 覆盖：先删旧 asset，再上传同名新 asset；
- 更新 Release 正文里的 SHA-256；
- Release URL 和 README 下载链接保持不变，旧 asset 的下载数会重新计数。

发布新版本时不要走这一节：新版本新建 tag 和 Release，旧版本保持原样。

## 注意

- 不要提交 `data\default`、`Settings\settings.json`、日志、截图。
- 不要提交 PAT/token；第一次 `git push` 由 Git Credential Manager 弹窗登录。
- 构建默认只出 en-US；中文版必须显式加 `-Variant zh-CN`。
- `说明.txt` 不发布；`scripts\说明模板` 是构建模板，保留。
- 当前 Release 只发两个免安装 zip；安装器源码保留，但不要把 setup exe 上传到 Release。
- 程序和 zip 未签名，首次运行仍可能出现 SmartScreen 提示；不要写成“已签名”或“不会提示”。
- 不要在本目录执行 `git clean -xdf`：`说明.txt`、`build`、`selfcheck-shots` 都在忽略列表里，`-x` 会把它们删掉。`git reset --hard` 也会覆盖未提交的源码；执行前先备份。
