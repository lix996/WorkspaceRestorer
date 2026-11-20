[CmdletBinding()]
param(
    [string]$AppPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $repoRoot 'product.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "缺少产品身份清单：$manifestPath"
}
$manifest = [System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$productId = [string]$manifest.technicalName
if ($productId -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
    throw "product.json 的 technicalName 不是合法 C# 标识符：$productId"
}

$legacyToken = 'ComputerStartupKeeper'
$legacyEnvironmentPrefix = 'COMPUTER_STARTUP_KEEPER_'
$sourceRoot = Join-Path $repoRoot ('src\' + $productId)
$testRoot = Join-Path $repoRoot 'tests'
$scriptRoot = Join-Path $repoRoot 'scripts'
$parentRoot = Split-Path -Path $repoRoot -Parent
$candidateDeliveryRoot = Join-Path $parentRoot 'zuizhongchanwu'
$deliveryRoot = if (Test-Path -LiteralPath $candidateDeliveryRoot -PathType Container) { $parentRoot } else { $repoRoot }
$appPath = if ([string]::IsNullOrWhiteSpace($AppPath)) {
    Join-Path $deliveryRoot ('zuizhongchanwu\' + $productId + '.exe')
}
else {
    [System.IO.Path]::GetFullPath($AppPath)
}
$failures = New-Object 'System.Collections.Generic.List[string]'

function Add-IdentityFailure {
    param([string]$Message)
    $failures.Add($Message)
}

if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) {
    Add-IdentityFailure "源码目录不存在：$sourceRoot"
}
else {
    foreach ($file in @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File -Filter '*.cs')) {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
        if ($text.Contains($legacyEnvironmentPrefix)) {
            Add-IdentityFailure ("源码仍含旧环境变量前缀：" + $file.FullName)
        }
        if ($file.Name -ne 'LegacyIdentity.cs' -and $text.Contains($legacyToken)) {
            Add-IdentityFailure ("源码仍含旧技术名（只允许 LegacyIdentity.cs）：" + $file.FullName)
        }
        foreach ($match in [regex]::Matches($text, '(?m)^\s*namespace\s+([A-Za-z_][A-Za-z0-9_.]*)')) {
            $namespace = $match.Groups[1].Value
            $root = $namespace.Split('.')[0]
            if ($root -cne $productId) {
                Add-IdentityFailure ("命名空间根不是 " + $productId + "：" + $file.FullName + " -> " + $namespace)
            }
        }
    }
}

if (Test-Path -LiteralPath $testRoot -PathType Container) {
    foreach ($file in @(Get-ChildItem -LiteralPath $testRoot -Recurse -File -Filter '*.cs')) {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
        if ($text.Contains($legacyToken)) {
            Add-IdentityFailure ("测试源码仍含旧技术名：" + $file.FullName)
        }
        if ($text.Contains($legacyEnvironmentPrefix)) {
            Add-IdentityFailure ("测试源码仍含旧环境变量前缀：" + $file.FullName)
        }
    }
}

# Build.ps1 和 Test.ps1 需要知道旧值才能做迁移和隔离检查；
# 其他自检脚本不允许再写旧 EXE、旧进程名或旧类型名。
$legacyAllowedScripts = @('Build.ps1', 'Test.ps1', 'Test-IdentityConsistency.ps1')
foreach ($file in @(Get-ChildItem -LiteralPath $scriptRoot -File -Filter '*.ps1')) {
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
    if ($text.Contains($legacyToken) -and ($legacyAllowedScripts -notcontains $file.Name)) {
        Add-IdentityFailure ("自检/工具脚本仍含旧技术名：" + $file.FullName)
    }
    if ($text.Contains($legacyEnvironmentPrefix) -and ($legacyAllowedScripts -notcontains $file.Name)) {
        Add-IdentityFailure ("自检/工具脚本仍含旧环境变量前缀：" + $file.FullName)
    }
}

if (Test-Path -LiteralPath $appPath -PathType Leaf) {
    $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($appPath).Name
    if ($assemblyName -cne $productId) {
        Add-IdentityFailure ("交付程序集名不是 " + $productId + "：" + $assemblyName)
    }

    $assembly = [System.Reflection.Assembly]::LoadFile($appPath)
    $identityType = $assembly.GetType($productId + '.Infrastructure.ProductIdentity', $true)
    foreach ($fieldName in @('TechnicalName', 'AppUserModelId', 'StartupRunValueName', 'ResourcePrefix', 'TestAssemblyName')) {
        $field = $identityType.GetField($fieldName, [System.Reflection.BindingFlags]'Public,NonPublic,Static')
        if ($null -eq $field) {
            Add-IdentityFailure ("产品程序集缺少 ProductIdentity." + $fieldName)
        }
    }
    if ($null -ne $identityType.GetField('TechnicalName', [System.Reflection.BindingFlags]'Public,NonPublic,Static')) {
        $value = [string]$identityType.GetField('TechnicalName', [System.Reflection.BindingFlags]'Public,NonPublic,Static').GetRawConstantValue()
        if ($value -cne $productId) {
            Add-IdentityFailure ("ProductIdentity.TechnicalName 与 product.json 不一致：" + $value)
        }
    }

    foreach ($resourceName in $assembly.GetManifestResourceNames()) {
        if (-not $resourceName.StartsWith($productId + '.Text.', [System.StringComparison]::Ordinal)) {
            Add-IdentityFailure ("嵌入资源前缀不是 " + $productId + ".Text.：" + $resourceName)
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host ("[FAIL] 身份一致性检查失败，共 " + $failures.Count + " 项：") -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host ("  - " + $failure) -ForegroundColor Red
    }
    exit 1
}

Write-Host ("[PASS] 身份一致性检查通过：技术名 " + $productId + "，源码/测试/脚本/程序集/资源前缀一致。") -ForegroundColor Green
exit 0
