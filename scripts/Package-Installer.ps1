[CmdletBinding()]
param(
    [ValidateSet('en-US', 'zh-CN', 'all')]
    [string]$Variant = 'en-US',
    [switch]$SkipBuild,
    [string]$Version,
    [string]$IsccPath,
    [string]$OutputRoot,
    [string]$SourceRevision
)

# Build the per-user installer(s) for WorkspaceRestorer.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\scripts\Package-Installer.ps1 -Variant all
#
# The installer shows Inno Setup's directory page, defaults to
# %LOCALAPPDATA%\Programs\WorkspaceRestorer, and never requests elevation.
# It writes fresh files, so the installed WorkspaceRestorer.exe has no
# Zone.Identifier. The downloaded setup itself can still carry one.

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$productManifestPath = Join-Path $repoRoot 'product.json'
if (-not (Test-Path -LiteralPath $productManifestPath -PathType Leaf)) {
    throw "Missing product manifest: $productManifestPath"
}
$productManifest = [System.IO.File]::ReadAllText($productManifestPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$productId = [string]$productManifest.technicalName
$sourceRoot = Join-Path $repoRoot ('src\' + $productId)
$appInfoPath = Join-Path $sourceRoot 'Shared\Infrastructure\AppInfo.cs'
$installerDir = Join-Path $PSScriptRoot 'installer'
$issPath = Join-Path $installerDir 'WorkspaceRestorer.iss'
$licensePath = Join-Path $repoRoot 'LICENSE'
$iconPath = Join-Path $repoRoot 'assets\app.ico'
$languagePath = Join-Path $installerDir 'ChineseSimplified.isl'
$buildRoot = Join-Path $repoRoot 'build'
$stageRoot = Join-Path $buildRoot 'installer\stage'

if (-not (Test-Path -LiteralPath $appInfoPath -PathType Leaf)) {
    throw "Missing AppInfo.cs: $appInfoPath"
}
if (-not (Test-Path -LiteralPath $issPath -PathType Leaf)) {
    throw "Missing installer script: $issPath"
}
if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
    throw "Missing LICENSE: $licensePath"
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
    throw "Missing app icon: $iconPath"
}
if (-not (Test-Path -LiteralPath $languagePath -PathType Leaf)) {
    throw "Missing Chinese language file: $languagePath"
}

if (-not $Version) {
    $appInfoText = [System.IO.File]::ReadAllText($appInfoPath, [System.Text.Encoding]::UTF8)
    $versionMatch = [regex]::Match($appInfoText, 'public const string Version = "([^"]+)"')
    if (-not $versionMatch.Success) {
        throw "Cannot read AppInfo.Version from $appInfoPath"
    }
    $Version = $versionMatch.Groups[1].Value
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be three numeric parts, for example 1.1.0: $Version"
}

if (-not $OutputRoot) {
    $OutputRoot = Join-Path $buildRoot 'installer'
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

if (-not $IsccPath) {
    $localAppData = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::LocalApplicationData)
    $programFiles = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFilesX86)
    $candidates = @()
    if ($localAppData) {
        $candidates += (Join-Path $localAppData 'Programs\Inno Setup 6\ISCC.exe')
    }
    if ($programFiles) {
        $candidates += (Join-Path $programFiles 'Inno Setup 6\ISCC.exe')
    }
    if ($programFilesX86) {
        $candidates += (Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe')
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $IsccPath = $candidate
            break
        }
    }
}
if (-not $IsccPath -or -not (Test-Path -LiteralPath $IsccPath -PathType Leaf)) {
    throw "ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath."
}

$commit = ''
$dirty = $false
try {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null).Trim()
    $status = @(& git -C $repoRoot status --porcelain 2>$null)
    $dirty = $status.Count -gt 0
} catch {
    $commit = ''
    $dirty = $true
}
if (-not $SourceRevision) {
    $SourceRevision = if ($commit) { $commit } else { 'unknown' }
}

function Assert-UnderRoot {
    param([string]$Path, [string]$Root)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootWithSeparator = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($rootWithSeparator, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside $Root : $fullPath"
    }
}

function Get-DeliveryExecutable {
    param([string]$VariantId)

    $variantPath = Join-Path $repoRoot ('variants\' + $VariantId + '.json')
    if (-not (Test-Path -LiteralPath $variantPath -PathType Leaf)) {
        throw "Missing variant file: $variantPath"
    }
    $variantJson = [System.IO.File]::ReadAllText($variantPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $deliveryFolder = [string]$variantJson.deliveryFolder
    if ([string]::IsNullOrWhiteSpace($deliveryFolder)) {
        throw "Variant $VariantId has no deliveryFolder."
    }

    $candidates = @(
        (Join-Path (Split-Path $repoRoot -Parent) (Join-Path $deliveryFolder ($productId + '.exe'))),
        (Join-Path $repoRoot (Join-Path $deliveryFolder ($productId + '.exe')))
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return $candidate
        }
    }
    throw "Missing built executable for $VariantId. Run Build.ps1 first or omit -SkipBuild."
}

function Write-SourceFile {
    param([string]$Path)

    $lines = New-Object 'System.Collections.Generic.List[string]'
    $lines.Add("WorkspaceRestorer v$Version")
    $lines.Add('')
    $lines.Add('Source code:')
    $lines.Add('https://github.com/lix996/WorkspaceRestorer')
    $lines.Add('')
    $lines.Add('Corresponding source revision:')
    if ($SourceRevision -match '^[0-9a-fA-F]{40}$') {
        $lines.Add('https://github.com/lix996/WorkspaceRestorer/tree/' + $SourceRevision)
    } else {
        $lines.Add($SourceRevision)
    }
    $lines.Add('')
    $lines.Add('License:')
    $lines.Add('GNU General Public License v3.0. See LICENSE.')
    if ($dirty) {
        $lines.Add('')
        $lines.Add('Packaging note: the working tree had uncommitted changes when this package was built; rebuild after committing before publishing.')
    }
    [System.IO.File]::WriteAllText($Path, ($lines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($true)))
}

$variantIds = if ($Variant -eq 'all') { @('en-US', 'zh-CN') } else { @($Variant) }

foreach ($variantId in $variantIds) {
    if (-not $SkipBuild) {
        Write-Host ("[BUILD] {0}" -f $variantId) -ForegroundColor Cyan
        & (Join-Path $PSScriptRoot 'Build.ps1') -Variant $variantId
    }

    $exePath = Get-DeliveryExecutable -VariantId $variantId
    $fileVersion = [string](Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
    if ($fileVersion -and -not $fileVersion.StartsWith($Version, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Built executable version ($fileVersion) does not match package version ($Version): $exePath"
    }
    $stageDir = Join-Path $stageRoot $variantId
    Assert-UnderRoot -Path $stageDir -Root $buildRoot
    if (Test-Path -LiteralPath $stageDir) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $stageDir 'data') -Force | Out-Null
    Copy-Item -LiteralPath $exePath -Destination (Join-Path $stageDir 'WorkspaceRestorer.exe') -Force
    Copy-Item -LiteralPath $iconPath -Destination (Join-Path $stageDir 'data\app.ico') -Force
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $stageDir 'LICENSE') -Force
    Write-SourceFile -Path (Join-Path $stageDir 'SOURCE.txt')

    Get-ChildItem -LiteralPath $stageDir -Recurse -File | ForEach-Object {
        Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue
    }

    $suffix = if ($variantId -eq 'zh-CN') { '-zh' } else { '' }
    $outputBaseName = "WorkspaceRestorer-Setup-v$Version-windows-x64$suffix"
    $defines = @(
        "/DMyAppName=WorkspaceRestorer",
        "/DMyAppVersion=$Version",
        "/DSourceExe=$exePath",
        "/DStageDir=$stageDir",
        "/DOutputDir=$OutputRoot",
        "/DOutputBaseFilename=$outputBaseName",
        "/DSetupIcon=$iconPath",
        "/DLicenseFile=$licensePath",
        "/DLanguageDir=$installerDir"
    )

    Write-Host ("[SETUP] {0}" -f $outputBaseName) -ForegroundColor Cyan
    & $IsccPath @defines $issPath
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC failed with exit code $LASTEXITCODE."
    }

    $setupPath = Join-Path $OutputRoot ($outputBaseName + '.exe')
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Expected setup was not created: $setupPath"
    }
    Unblock-File -LiteralPath $setupPath -ErrorAction SilentlyContinue
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash
    $size = (Get-Item -LiteralPath $setupPath).Length
    Write-Host ("[OK] {0} ({1} bytes) SHA-256 {2}" -f $setupPath, $size, $hash) -ForegroundColor Green
}
