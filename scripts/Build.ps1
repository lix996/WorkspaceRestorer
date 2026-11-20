[CmdletBinding()]
param(
    [switch]$Clean,
    [string]$Variant = 'en-US'
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$productManifestPath = Join-Path $repoRoot 'product.json'
if (-not (Test-Path -LiteralPath $productManifestPath -PathType Leaf)) {
    throw "缺少产品身份清单：$productManifestPath"
}
$productManifest = [System.IO.File]::ReadAllText($productManifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$productId = [string]$productManifest.technicalName
if ($productId -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
    throw "product.json 的 technicalName 不是合法 C# 标识符：$productId"
}
$sourceRoot = Join-Path $repoRoot ('src\' + $productId)
$buildRoot = Join-Path $repoRoot 'build\app'
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputPath = Join-Path $buildRoot ($productId + '.exe')
$responseFile = Join-Path $buildRoot ($productId + '.rsp')
$generatedRoot = Join-Path $buildRoot 'generated'
$generatedIdentityPath = Join-Path $generatedRoot 'ProductIdentity.g.cs'
$generatedLayoutPath = Join-Path $generatedRoot 'DataLayout.g.cs'

# ---------------------------------------------------------------------------
# 构建变体（固定数据目录名用哪一套）
#   唯一来源：variants\<变体>.json。默认 en-US，交付到 zuizhongchanwu；
#   zh-CN 交付到 zuizhongchanwu-zh。两个产物共用同一份源码，代码里不写死目录名。
# ---------------------------------------------------------------------------
$variantRoot = Join-Path $repoRoot 'variants'
if (-not (Test-Path -LiteralPath $variantRoot -PathType Container)) {
    throw "缺少目录名方案目录：$variantRoot"
}
$variantFiles = @(Get-ChildItem -LiteralPath $variantRoot -File -Filter '*.json' | Sort-Object -Property Name)
if ($variantFiles.Count -eq 0) {
    throw "目录名方案目录里没有 JSON：$variantRoot"
}

# 顺序必须和 DataLayout 类的字段一致。
$layoutKeys = @(
    'workflows', 'startupRestore', 'desktopLayout', 'applications',
    'startupProfiles', 'layouts', 'applicationPlans',
    'recycleBin', 'settings', 'logs', 'backup', 'temp',
    'autoRestore', 'startupProfileBackups', 'legacy', 'unrecognized'
)
$layoutFields = @(
    'Workflows', 'StartupRestore', 'DesktopLayout', 'Applications',
    'StartupProfiles', 'Layouts', 'ApplicationPlans',
    'RecycleBin', 'Settings', 'Logs', 'Backup', 'Temp',
    'AutoRestore', 'StartupProfileBackups', 'Legacy', 'Unrecognized'
)

$allVariants = New-Object 'System.Collections.Generic.List[object]'
foreach ($variantFile in $variantFiles) {
    $variantJson = [System.IO.File]::ReadAllText($variantFile.FullName, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $variantId = [string]$variantJson.variant
    if ([string]::IsNullOrWhiteSpace($variantId)) {
        $variantId = [System.IO.Path]::GetFileNameWithoutExtension($variantFile.Name)
    }
    if ($variantId -notmatch '^[A-Za-z][A-Za-z0-9-]*$') {
        throw "变体名不合法（$($variantFile.Name)）：$variantId"
    }
    $delivery = [string]$variantJson.deliveryFolder
    if ($delivery -notmatch '^[A-Za-z0-9_-]+$') {
        throw "变体 $variantId 的 deliveryFolder 不合法：$delivery"
    }
    $foldersNode = $variantJson.folders
    if ($null -eq $foldersNode) {
        throw "变体 $variantId 缺少 folders 节点。"
    }

    $names = New-Object 'System.Collections.Generic.List[string]'
    $pairs = New-Object 'System.Collections.Generic.List[object]'
    for ($index = 0; $index -lt $layoutKeys.Count; $index++) {
        $property = $foldersNode.PSObject.Properties[$layoutKeys[$index]]
        if ($null -eq $property) {
            throw "变体 $variantId 缺少目录名：$($layoutKeys[$index])"
        }
        $value = [string]$property.Value
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "变体 $variantId 的目录名为空：$($layoutKeys[$index])"
        }
        if ($value -match '[\\/:*?"<>|]') {
            throw "变体 $variantId 的目录名含非法字符：$($layoutKeys[$index]) = $value"
        }
        $names.Add($value)
        $pairs.Add([pscustomobject]@{ Key = $layoutKeys[$index]; Field = $layoutFields[$index]; Value = $value })
    }

    # 同一层里两个固定目录同名会互相覆盖，必须提前拦住。
    for ($left = 0; $left -lt $names.Count; $left++) {
        for ($right = $left + 1; $right -lt $names.Count; $right++) {
            if ([string]::Equals($names[$left], $names[$right], [System.StringComparison]::OrdinalIgnoreCase)) {
                throw ("变体 $variantId 的目录名重复：{0} 和 {1} 都是 {2}" -f $layoutKeys[$left], $layoutKeys[$right], $names[$left])
            }
        }
    }

    $allVariants.Add([pscustomobject]@{
        Id = $variantId
        Delivery = $delivery
        Source = $variantFile.Name
        Pairs = $pairs
    })
}

$activeVariant = $allVariants | Where-Object { $_.Id -ieq $Variant } | Select-Object -First 1
if ($null -eq $activeVariant) {
    $available = ($allVariants | ForEach-Object { $_.Id }) -join ', '
    throw "未知的构建变体：$Variant（可用：$available）"
}

$parentRoot = Split-Path -Path $repoRoot -Parent
$candidateDeliveryRoot = Join-Path $parentRoot $activeVariant.Delivery
$deliveryRoot = if (Test-Path -LiteralPath $candidateDeliveryRoot -PathType Container) { $parentRoot } else { $repoRoot }
$finalRoot = Join-Path $deliveryRoot $activeVariant.Delivery
$finalPath = Join-Path $finalRoot ($productId + '.exe')

function Assert-PathInsideRoot {
    param([string]$Path, [string]$Root)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = $Root.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝操作根目录外路径：$fullPath（根：$Root）"
    }
}

function Quote-ResponsePath {
    param([string]$Path)
    return '"' + $Path.Replace('"', '\"') + '"'
}

if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    throw "未找到要求的 .NET Framework 编译器：$compilerPath"
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    throw "未找到源码目录：$sourceRoot"
}

Assert-PathInsideRoot -Path $buildRoot -Root $repoRoot
Assert-PathInsideRoot -Path $finalRoot -Root $deliveryRoot

# 交付时 EXE 不能被正在运行的同名进程占用；更不要静默强杀用户正在用的程序。
$running = @(Get-Process -Name @($productId, 'ComputerStartupKeeper') -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $names = ($running | ForEach-Object { $_.ProcessName + ' (PID ' + $_.Id + ')' }) -join ', '
    throw "构建前请先退出正在运行的程序：$names"
}

if ($Clean -and (Test-Path -LiteralPath $buildRoot)) {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
New-Item -ItemType Directory -Path $generatedRoot -Force | Out-Null
New-Item -ItemType Directory -Path $finalRoot -Force | Out-Null

# 技术身份的唯一来源是根目录 product.json。这里生成编译期常量，
# 让资源前缀、AUMID、注册表值、互斥体名和测试程序集名不再各写一份。
$generatedIdentity = @"
namespace $productId.Infrastructure
{
    internal static class ProductIdentity
    {
        internal const string TechnicalName = "$productId";
        internal const string AppUserModelId = TechnicalName + ".Desktop";
        internal const string StartupRunValueName = TechnicalName;
        internal const string ResourcePrefix = TechnicalName + ".Text.";
        internal const string SingleInstanceMutexName = TechnicalName;
        internal const string ActivationSignalName = @"Local\" + TechnicalName + ".ActivateSignal";
        internal const string TestAssemblyName = TechnicalName + ".Tests";
        internal const string SessionCaptureTempPrefix = TechnicalName + ".SessionCapture.";
        internal const string ToastTempPrefix = "workspace-restorer-toast-";
    }
}
"@
[System.IO.File]::WriteAllText($generatedIdentityPath, $generatedIdentity, (New-Object System.Text.UTF8Encoding($true)))

# 固定数据目录名：所有变体都生成进程序（迁移要用另一套名字），当前构建的那套另外生成常量。
function ConvertTo-CSharpString {
    param([string]$Text)

    return '"' + $Text.Replace('\', '\\').Replace('"', '\"') + '"'
}

$layoutBuilder = New-Object System.Text.StringBuilder
[void]$layoutBuilder.AppendLine('// 由 scripts\Build.ps1 根据 variants\*.json 生成，不要手改。')
[void]$layoutBuilder.AppendLine('namespace ' + $productId + '.Infrastructure')
[void]$layoutBuilder.AppendLine('{')
[void]$layoutBuilder.AppendLine('    internal static class ActiveDataLayout')
[void]$layoutBuilder.AppendLine('    {')
[void]$layoutBuilder.AppendLine('        internal const string Variant = ' + (ConvertTo-CSharpString $activeVariant.Id) + ';')
foreach ($pair in $activeVariant.Pairs) {
    [void]$layoutBuilder.AppendLine('        internal const string ' + $pair.Field + ' = ' + (ConvertTo-CSharpString $pair.Value) + ';')
}
[void]$layoutBuilder.AppendLine('    }')
[void]$layoutBuilder.AppendLine()
[void]$layoutBuilder.AppendLine('    internal static class DataLayoutTable')
[void]$layoutBuilder.AppendLine('    {')
[void]$layoutBuilder.AppendLine('        internal static readonly DataLayout[] All = new DataLayout[]')
[void]$layoutBuilder.AppendLine('        {')
foreach ($layoutVariant in $allVariants) {
    [void]$layoutBuilder.AppendLine('            new DataLayout')
    [void]$layoutBuilder.AppendLine('            {')
    [void]$layoutBuilder.AppendLine('                Variant = ' + (ConvertTo-CSharpString $layoutVariant.Id) + ',')
    foreach ($pair in $layoutVariant.Pairs) {
        [void]$layoutBuilder.AppendLine('                ' + $pair.Field + ' = ' + (ConvertTo-CSharpString $pair.Value) + ',')
    }
    [void]$layoutBuilder.AppendLine('            },')
}
[void]$layoutBuilder.AppendLine('        };')
[void]$layoutBuilder.AppendLine()
[void]$layoutBuilder.AppendLine('        internal static DataLayout Find(string variant)')
[void]$layoutBuilder.AppendLine('        {')
[void]$layoutBuilder.AppendLine('            for (int index = 0; index < All.Length; index++)')
[void]$layoutBuilder.AppendLine('            {')
[void]$layoutBuilder.AppendLine('                if (string.Equals(All[index].Variant, variant, System.StringComparison.OrdinalIgnoreCase))')
[void]$layoutBuilder.AppendLine('                {')
[void]$layoutBuilder.AppendLine('                    return All[index];')
[void]$layoutBuilder.AppendLine('                }')
[void]$layoutBuilder.AppendLine('            }')
[void]$layoutBuilder.AppendLine('            return All.Length > 0 ? All[0] : null;')
[void]$layoutBuilder.AppendLine('        }')
[void]$layoutBuilder.AppendLine('    }')
[void]$layoutBuilder.AppendLine('}')
[System.IO.File]::WriteAllText($generatedLayoutPath, $layoutBuilder.ToString(), (New-Object System.Text.UTF8Encoding($true)))
Write-Host ("构建变体：{0}（固定目录名 {1} 套）→ {2}" -f $activeVariant.Id, $allVariants.Count, $activeVariant.Delivery) -ForegroundColor Cyan

# 项目约定：每个目录都要有自己的“说明.txt”。build 目录由本脚本生成。
# 说明正文统一放在 scripts\说明模板，只有目标文件不存在时才写入；
# 这样手工修改的说明不会被构建覆盖，-Clean 删掉 build\app 后又能自动补回完整说明。
function Write-NoteIfMissing {
    param([string]$NotePath, [string]$TemplatePath)

    if (Test-Path -LiteralPath $NotePath -PathType Leaf) { return }
    if (-not (Test-Path -LiteralPath $TemplatePath -PathType Leaf)) {
        throw "缺少说明模板：$TemplatePath"
    }
    $templateText = [System.IO.File]::ReadAllText($TemplatePath, [System.Text.Encoding]::UTF8)
    [System.IO.File]::WriteAllText($NotePath, $templateText, (New-Object System.Text.UTF8Encoding($true)))
}

Write-NoteIfMissing -NotePath (Join-Path (Split-Path -Path $buildRoot -Parent) '说明.txt') -TemplatePath (Join-Path $PSScriptRoot '说明模板\build目录.txt')
Write-NoteIfMissing -NotePath (Join-Path $buildRoot '说明.txt') -TemplatePath (Join-Path $PSScriptRoot '说明模板\build-app目录.txt')

$xamlFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.xaml')
if ($xamlFiles.Count -gt 0) {
    throw '当前 csc 构建流程不执行 WPF XAML 标记编译。请使用纯 C# WPF，或先正式迁移到 MSBuild。'
}

$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs' | Sort-Object -Property FullName)
if ($sourceFiles.Count -eq 0) {
    throw "源码目录中没有 .cs 文件：$sourceRoot"
}

$programFilesX86 = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFilesX86)
$referenceRoot = Join-Path $programFilesX86 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
if (-not (Test-Path -LiteralPath $referenceRoot -PathType Container)) {
    # The Windows image used for portable builds may not include the Developer Pack.
    # Framework 4.x reference-compatible assemblies in the CLR directory are sufficient
    # for this source-only C# 5 build and keep the EXE dependency-free.
    $referenceRoot = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
}

$referenceNames = @(
    'Accessibility.dll',
    'Microsoft.CSharp.dll',
    'PresentationCore.dll',
    'PresentationFramework.dll',
    'System.dll',
    'System.Configuration.dll',
    'System.Core.dll',
    'System.Drawing.dll',
    'System.Runtime.Serialization.dll',
    'System.Web.Extensions.dll',
    'System.Windows.Forms.dll',
    'System.Xaml.dll',
    'System.Xml.dll',
    'System.Xml.Linq.dll',
    'UIAutomationClient.dll',
    'UIAutomationProvider.dll',
    'UIAutomationTypes.dll',
    'WindowsBase.dll'
)

$compilerArguments = New-Object 'System.Collections.Generic.List[string]'
$compilerArguments.Add('/nologo')
$compilerArguments.Add('/target:winexe')
$compilerArguments.Add('/platform:x64')
$compilerArguments.Add('/optimize+')
$compilerArguments.Add('/debug:pdbonly')
$compilerArguments.Add('/langversion:5')
$compilerArguments.Add('/warn:4')
$compilerArguments.Add('/out:' + (Quote-ResponsePath -Path $outputPath))

# 图标只有一个源码源：`assets\app.ico`（git 跟踪）。
# 构建时把它嵌进 exe，并复制到 `zuizhongchanwu\data\app.ico` 供窗口/托盘运行时读取。
# data 根下不是 data\default，删 default 清私人数据不会把图标删掉。
$appIconPath = Join-Path $repoRoot 'assets\app.ico'
$deliveryAppIconPath = Join-Path $finalRoot 'data\app.ico'
if (Test-Path -LiteralPath $appIconPath -PathType Leaf) {
    $compilerArguments.Add('/win32icon:' + (Quote-ResponsePath -Path $appIconPath))
}
else {
    Write-Host ("[图标] 没找到 {0}，这个 exe 会没有图标（先跑 scripts\Make-AppIcon.ps1 生成）。" -f $appIconPath) -ForegroundColor Yellow
}

foreach ($referenceName in $referenceNames) {
    $referencePath = Join-Path $referenceRoot $referenceName
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        # WPF assemblies are installed in the GAC on machines without the Developer Pack.
        $gacCandidates = @(Get-ChildItem -Path 'C:\Windows\Microsoft.NET\assembly' -Recurse -File -Filter $referenceName -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq $referenceName } |
            Sort-Object FullName -Descending)
        if ($gacCandidates.Count -gt 0) {
            $referencePath = $gacCandidates[0].FullName
        }
        else {
            throw "缺少引用程序集：$referencePath"
        }
    }
    $compilerArguments.Add('/reference:' + (Quote-ResponsePath -Path $referencePath))
}

foreach ($sourceFile in $sourceFiles) {
    $compilerArguments.Add((Quote-ResponsePath -Path $sourceFile.FullName))
}
$compilerArguments.Add((Quote-ResponsePath -Path $generatedIdentityPath))
$compilerArguments.Add((Quote-ResponsePath -Path $generatedLayoutPath))

# 界面语言资源：每种语言、每个功能一个子目录和一个同名 JSON，嵌进 EXE。
# 逻辑名必须和 AppText.ResourcePrefix（ProductIdentity.ResourcePrefix）一致。
$localizationRoot = Join-Path $sourceRoot 'Shared\UI\Text\Localization\Resources'
if (-not (Test-Path -LiteralPath $localizationRoot -PathType Container)) {
    throw "缺少语言资源目录：$localizationRoot"
}
$languageFiles = @(Get-ChildItem -LiteralPath $localizationRoot -Recurse -File -Filter '*.json' | Sort-Object -Property FullName)
if ($languageFiles.Count -eq 0) {
    throw "语言资源目录中没有 JSON：$localizationRoot"
}
foreach ($languageFile in $languageFiles) {
    $relative = $languageFile.FullName.Substring($localizationRoot.Length).TrimStart('\')
    $logicalName = $productId + '.Text.' + ($relative -replace '\\', '.')
    $compilerArguments.Add('/resource:' + (Quote-ResponsePath -Path $languageFile.FullName) + ',' + $logicalName)
}

$utf8WithBom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllLines($responseFile, $compilerArguments.ToArray(), $utf8WithBom)

Write-Host "正在编译 $($sourceFiles.Count) 个 C# 源文件..."
& $compilerPath "@$responseFile"
if ($LASTEXITCODE -ne 0) {
    throw "编译失败，csc 退出代码：$LASTEXITCODE"
}

Copy-Item -LiteralPath $outputPath -Destination $finalPath -Force

# 交付副本：窗口/托盘运行时读 data\app.ico；源码资产和交付副本保持同一份字节。
if (Test-Path -LiteralPath $appIconPath -PathType Leaf) {
    $deliveryDataRoot = Join-Path $finalRoot 'data'
    New-Item -ItemType Directory -Path $deliveryDataRoot -Force | Out-Null
    Copy-Item -LiteralPath $appIconPath -Destination $deliveryAppIconPath -Force
    Write-Host ("已同步交付图标：{0}" -f $deliveryAppIconPath) -ForegroundColor DarkGray
}

# 用户第 93 条：运行 exe 时弹“打开文件 - 安全警告 / 无法验证发布者”。
# 这是 Windows 对**带“来自 Internet”标记（Zone.Identifier）的文件**或**未签名程序**的提示。
# exe 由本机构建、来源可信，所以这里构建完就顺手把可能的下载标记去掉，
# 免得用户每次运行都被问一遍（对没有标记的文件调用是无害的）。
try {
    Unblock-File -LiteralPath $finalPath -ErrorAction SilentlyContinue
    Write-Host "已清除 exe 的“来自 Internet”标记（有则清，无则跳过）。" -ForegroundColor DarkGray
} catch {
    Write-Host "清除下载标记时跳过：$($_.Exception.Message)" -ForegroundColor DarkGray
}

$appConfigPath = Join-Path $sourceRoot 'App.config'
if (Test-Path -LiteralPath $appConfigPath -PathType Leaf) {
    Copy-Item -LiteralPath $appConfigPath -Destination ($finalPath + '.config') -Force
}

Write-Host "构建成功：$finalPath" -ForegroundColor Green
