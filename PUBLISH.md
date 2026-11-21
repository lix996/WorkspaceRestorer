# 发布到 GitHub

- 仓库：https://github.com/lix996/WorkspaceRestorer
- 可见性：公开
- 默认分支：main
- 许可证：MIT
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
- `scripts\说明模板\**`
- `README.md`、`README.zh-CN.md`、`PUBLISH.md`、`LICENSE`、`.gitignore`、`.gitattributes`

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

1. 构建英文：`powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1`
2. 构建中文：`powershell -ExecutionPolicy Bypass -File .\scripts\Build.ps1 -Variant zh-CN`
3. 打包两个 zip：只放 `WorkspaceRestorer.exe` 和 `data\app.ico`，不要带 `data\default`、设置、日志或备份。

   ```text
   WorkspaceRestorer-v1.0.0-windows-x64.zip
     WorkspaceRestorer-v1.0.0\WorkspaceRestorer.exe
     WorkspaceRestorer-v1.0.0\data\app.ico

   WorkspaceRestorer-v1.0.0-windows-x64-zh.zip
     WorkspaceRestorer-v1.0.0-zh\WorkspaceRestorer.exe
     WorkspaceRestorer-v1.0.0-zh\data\app.ico
   ```

4. 在 GitHub 的 Releases 新建 tag（例如 `v1.0.0`），上传两个 zip；exe 和 zip 都不提交进仓库。
5. `AppInfo.Version`、zip 文件名、内层目录名和 Release tag 必须保持一致。

### 重新发布同一版本

同一版本号需要替换安装包时：

- 不要删、不要移动已有 tag；
- 编辑已有 Release，不要新建另一个同名 tag；
- GitHub 不允许同名 asset 覆盖：先删旧 zip，再上传同名新 zip；
- 更新 Release 正文里的 SHA-256；
- Release URL 和 README 下载链接保持不变，旧 asset 的下载数会重新计数。

## 注意

- 不要提交 `data\default`、`Settings\settings.json`、日志、截图。
- 不要提交 PAT/token；第一次 `git push` 由 Git Credential Manager 弹窗登录。
- 构建默认只出 en-US；中文版必须显式加 `-Variant zh-CN`。
- `说明.txt` 不发布；`scripts\说明模板` 是构建模板，保留。
- 不要在本目录执行 `git clean -xdf`：`说明.txt`、`build`、`selfcheck-shots` 都在忽略列表里，`-x` 会把它们删掉。`git reset --hard` 也会覆盖未提交的源码；执行前先备份。
