[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$AppPath,
    [string]$Variant = 'en-US'
)

Set-StrictMode -Version 2.0
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
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
$buildScript = Join-Path $PSScriptRoot 'Build.ps1'
$variantPath = Join-Path $repoRoot ('variants\' + $Variant + '.json')
if (-not (Test-Path -LiteralPath $variantPath -PathType Leaf)) {
    throw "未知的构建变体：$Variant（缺少 $variantPath）"
}
$variantJson = [System.IO.File]::ReadAllText($variantPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
$parentRoot = Split-Path -Path $repoRoot -Parent
$candidateDeliveryRoot = Join-Path $parentRoot ([string]$variantJson.deliveryFolder)
$deliveryRoot = if (Test-Path -LiteralPath $candidateDeliveryRoot -PathType Container) { $parentRoot } else { $repoRoot }
$appPath = if ([string]::IsNullOrWhiteSpace($AppPath)) {
    Join-Path $deliveryRoot ([string]$variantJson.deliveryFolder + '\' + $productId + '.exe')
}
else {
    [System.IO.Path]::GetFullPath($AppPath)
}
$testSourceRoot = Join-Path $repoRoot 'tests'
$testBuildRoot = Join-Path $repoRoot 'build\tests'
$compilerPath = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testRunnerPath = Join-Path $testBuildRoot ($productId + '.Tests.exe')
$responseFile = Join-Path $testBuildRoot ($productId + '.Tests.rsp')
$sandboxRoot = Join-Path ([System.IO.Path]::GetTempPath()) ($productId + '.Tests.' + [System.Guid]::NewGuid().ToString('N'))
$dataRoot = Join-Path $sandboxRoot 'data'
$stdoutPath = Join-Path $sandboxRoot 'test.stdout.txt'
$stderrPath = Join-Path $sandboxRoot 'test.stderr.txt'

function Quote-ResponsePath {
    param([string]$Path)
    return '"' + $Path.Replace('"', '\"') + '"'
}

function Get-DesktopSignature {
    $desktopPath = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::DesktopDirectory)
    if ([string]::IsNullOrEmpty($desktopPath) -or -not (Test-Path -LiteralPath $desktopPath)) {
        return '<desktop-unavailable>'
    }

    $entries = @(Get-ChildItem -LiteralPath $desktopPath -Force -Recurse -ErrorAction Stop | ForEach-Object {
        $relativePath = $_.FullName.Substring($desktopPath.Length).TrimStart('\')
        $length = if ($_.PSIsContainer) { -1 } else { $_.Length }
        '{0}|{1}|{2}|{3}|{4}' -f $relativePath, $_.PSIsContainer, $length, $_.LastWriteTimeUtc.Ticks, [int]$_.Attributes
    } | Sort-Object)

    return ($entries -join "`n")
}

function Invoke-RegQuery {
    param([string]$Arguments)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'C:\Windows\System32\reg.exe'
    $startInfo.Arguments = $Arguments
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    return ('exit={0}' -f $process.ExitCode) + "`n" + $standardOutput + "`n" + $standardError
}

function Get-RegistrySignature {
    $queries = @(
        ('query HKCU\Software\' + $productId + ' /s'),
        ('query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ' + $productId),
        'query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v ComputerStartupKeeper',
        'query HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop /s',
        'query HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Streams\Desktop /s'
    )

    return (($queries | ForEach-Object { $_ + "`n" + (Invoke-RegQuery -Arguments $_) }) -join "`n---`n")
}

function Restore-ProcessEnvironment {
    param([hashtable]$PreviousValues)

    foreach ($name in $PreviousValues.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $PreviousValues[$name], [System.EnvironmentVariableTarget]::Process)
    }
}

if (-not $SkipBuild) {
    & $buildScript -Variant $Variant
}
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
    throw "未找到待测程序：$appPath"
}
$identityCheck = Join-Path $PSScriptRoot 'Test-IdentityConsistency.ps1'
if (-not (Test-Path -LiteralPath $identityCheck -PathType Leaf)) {
    throw "缺少身份一致性检查：$identityCheck"
}
& $identityCheck -AppPath $appPath
if ($LASTEXITCODE -ne 0) {
    throw "身份一致性检查失败。"
}
if (-not (Test-Path -LiteralPath $compilerPath -PathType Leaf)) {
    throw "未找到要求的 .NET Framework 编译器：$compilerPath"
}

New-Item -ItemType Directory -Path $testBuildRoot -Force | Out-Null
New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null

# 与 Build.ps1 相同的约定：说明模板放在 scripts\说明模板，只有目标文件不存在时才写入，
# 避免每次测试把手工改过的说明覆盖回旧的一句话。
if (-not (Test-Path -LiteralPath (Join-Path $testBuildRoot '说明.txt') -PathType Leaf)) {
    $noteTemplatePath = Join-Path $PSScriptRoot '说明模板\build-tests目录.txt'
    if (-not (Test-Path -LiteralPath $noteTemplatePath -PathType Leaf)) {
        throw "缺少说明模板：$noteTemplatePath"
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $testBuildRoot '说明.txt'),
        [System.IO.File]::ReadAllText($noteTemplatePath, [System.Text.Encoding]::UTF8),
        (New-Object System.Text.UTF8Encoding($true)))
}

$testSources = @(Get-ChildItem -LiteralPath $testSourceRoot -Recurse -File -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\DialogLab\\' } |
    Sort-Object -Property FullName)
if ($testSources.Count -eq 0) {
    throw "没有测试源码：$testSourceRoot"
}

$programFilesX86 = [System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::ProgramFilesX86)
$referenceRoot = Join-Path $programFilesX86 'Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$frameworkAssemblyRoot = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$compilerArguments = New-Object 'System.Collections.Generic.List[string]'
$compilerArguments.Add('/nologo')
$compilerArguments.Add('/target:exe')
$compilerArguments.Add('/platform:anycpu')
$compilerArguments.Add('/langversion:5')
$compilerArguments.Add('/warn:4')
$compilerArguments.Add('/out:' + (Quote-ResponsePath -Path $testRunnerPath))
foreach ($referenceName in @('System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'WindowsBase.dll', 'PresentationCore.dll', 'PresentationFramework.dll', 'System.Xaml.dll')) {
    $referencePath = Join-Path $referenceRoot $referenceName
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        $referencePath = Join-Path $frameworkAssemblyRoot $referenceName
    }
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        $gacCandidates = @(Get-ChildItem -Path 'C:\Windows\Microsoft.NET\assembly' -Recurse -File -Filter $referenceName -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ieq $referenceName } |
            Sort-Object FullName -Descending)
        if ($gacCandidates.Count -gt 0) {
            $referencePath = $gacCandidates[0].FullName
        }
    }
    if (-not (Test-Path -LiteralPath $referencePath -PathType Leaf)) {
        throw "缺少测试引用程序集：$referenceName"
    }
    $compilerArguments.Add('/reference:' + (Quote-ResponsePath -Path $referencePath))
}
$compilerArguments.Add('/reference:' + (Quote-ResponsePath -Path $appPath))
foreach ($testSource in $testSources) {
    $compilerArguments.Add((Quote-ResponsePath -Path $testSource.FullName))
}

$utf8WithBom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllLines($responseFile, $compilerArguments.ToArray(), $utf8WithBom)
& $compilerPath "@$responseFile"
if ($LASTEXITCODE -ne 0) {
    throw "测试编译失败，csc 退出代码：$LASTEXITCODE"
}

$environmentPrefix = ($productId -creplace '([a-z0-9])([A-Z])', '$1_$2').ToUpperInvariant() + '_'
$environmentNames = @(
    $environmentPrefix + 'TEST_MODE',
    $environmentPrefix + 'DATA_DIR',
    $environmentPrefix + 'DISABLE_EXTERNAL_EFFECTS'
)
$previousEnvironment = @{}
foreach ($environmentName in $environmentNames) {
    $previousEnvironment[$environmentName] = [System.Environment]::GetEnvironmentVariable($environmentName, [System.EnvironmentVariableTarget]::Process)
}

$desktopBefore = $null
$registryBefore = $null
$watcher = $null
$testProcess = $null
$startedChildren = New-Object 'System.Collections.Generic.List[string]'
$testExitCode = -1

try {
    [System.Environment]::SetEnvironmentVariable($environmentPrefix + 'TEST_MODE', '1', [System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable($environmentPrefix + 'DATA_DIR', $dataRoot, [System.EnvironmentVariableTarget]::Process)
    [System.Environment]::SetEnvironmentVariable($environmentPrefix + 'DISABLE_EXTERNAL_EFFECTS', '1', [System.EnvironmentVariableTarget]::Process)

    $desktopBefore = Get-DesktopSignature
    $registryBefore = Get-RegistrySignature

    Add-Type -AssemblyName System.Management
    $watcher = New-Object System.Management.ManagementEventWatcher('SELECT * FROM Win32_ProcessStartTrace')
    $watcher.Options.Timeout = [System.TimeSpan]::FromMilliseconds(250)
    try {
        $watcher.Start()
    }
    catch {
        # Some managed sandboxes deny WMI process tracing.  The rest of the
        # isolation guard remains active; process tracing is an extra check.
        $watcher.Dispose()
        $watcher = $null
    }

    $quotedAppPath = '"' + $appPath.Replace('"', '\"') + '"'
    $testProcess = Start-Process -FilePath $testRunnerPath -ArgumentList $quotedAppPath -PassThru -Wait -NoNewWindow -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    # -Wait ensures Windows PowerShell refreshes the process exit code even
    # when output is redirected to files.
    $testProcess.WaitForExit()
    $testProcess.Refresh()
    $testExitCode = $testProcess.ExitCode

    if ($null -ne $watcher) {
        while ($true) {
            try {
                $processEvent = $watcher.WaitForNextEvent()
                if ([uint32]$processEvent.ParentProcessID -eq [uint32]$testProcess.Id) {
                    $startedChildren.Add(('{0} (PID {1})' -f $processEvent.ProcessName, $processEvent.ProcessID))
                }
            }
            catch [System.Management.ManagementException] {
                break
            }
        }
    }

    if (Test-Path -LiteralPath $stdoutPath) {
        Get-Content -LiteralPath $stdoutPath | ForEach-Object { Write-Host $_ }
    }
    if (Test-Path -LiteralPath $stderrPath) {
        Get-Content -LiteralPath $stderrPath | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    }
    # 失败清单其实是走 stderr 回来的；控制台编码会把它显示成乱码，
    # 所以这里额外写一份 UTF-8 文本，方便直接看是哪几项失败。
    if (Test-Path -LiteralPath $stderrPath) {
        try {
            $failText = Get-Content -LiteralPath $stderrPath -Raw
            [System.IO.File]::WriteAllText(
                (Join-Path $testBuildRoot '测试失败清单.txt'),
                $failText,
                (New-Object System.Text.UTF8Encoding($false)))
        } catch { }
    }

    $desktopAfter = Get-DesktopSignature
    $registryAfter = Get-RegistrySignature

    if ($desktopBefore -cne $desktopAfter) {
        throw '测试期间桌面文件或目录元数据发生变化。测试被判定为不隔离。'
    }
    if ($registryBefore -cne $registryAfter) {
        throw '测试期间程序启动项或桌面布局相关注册表状态发生变化。测试被判定为不隔离。'
    }
    if ($startedChildren.Count -gt 0) {
        throw ('测试运行器启动了不允许的子进程：' + ($startedChildren -join ', '))
    }
    if ($testExitCode -ne 0) {
        throw "测试失败，运行器退出代码：$testExitCode"
    }

    Write-Host '全部测试通过，外部副作用护栏未发现变化。' -ForegroundColor Green
}
finally {
    if ($null -ne $watcher) {
        try { $watcher.Stop() } catch { }
        $watcher.Dispose()
    }
    Restore-ProcessEnvironment -PreviousValues $previousEnvironment

    $fullSandboxRoot = [System.IO.Path]::GetFullPath($sandboxRoot)
    $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($fullSandboxRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $fullSandboxRoot)) {
        $removed = $false
        for ($attempt = 0; $attempt -lt 5 -and -not $removed; $attempt++) {
            try {
                Remove-Item -LiteralPath $fullSandboxRoot -Recurse -Force -ErrorAction Stop
                $removed = $true
            }
            catch {
                # The test process's redirected stderr/stdout handles can take
                # a moment to be released on some hosts. Retry before giving up
                # so a stale temp directory never fails an otherwise green run.
                Start-Sleep -Milliseconds 400
            }
        }
    }
}
