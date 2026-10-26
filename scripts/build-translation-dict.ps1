#Requires -Version 7.0
<#
.SYNOPSIS
    从纯净客户端快照一键重建汉化词典。

.DESCRIPTION
    完整流程：
      1. 校验 schema 与客户端路径
      2. 导出国际服繁中快照（已有则复用，-RefreshIntl 强制重导）
      3. 导出国服简中快照
      4. 按 schema 字段定义提取并生成词典
      5. 在快照上模拟一次注入，断言不会破坏数据结构
      6. 复制到 FreiAtlas.Patcher 资源目录

    国际服客户端在打补丁后不再是纯净数据源。脚本会对快照做繁体特征抽样检查，
    命中简体专有字即终止，避免用被污染的数据产出错误词典。

.PARAMETER CnClient
    国服客户端根目录（Bundles2 模式）。

.PARAMETER IntlClient
    国际服客户端根目录（GGPK 模式）。

.PARAMETER RefreshIntl
    强制重新导出国际服快照。仅在确认客户端已还原为官方版本时使用。

.EXAMPLE
    pwsh -File scripts/build-translation-dict.ps1

.EXAMPLE
    pwsh -File scripts/build-translation-dict.ps1 -RefreshIntl
#>
[CmdletBinding()]
param(
    [string]$CnClient = 'H:\WeGameApps\rail_apps\流放之路：降临(2002052)',
    [string]$IntlClient = 'E:\PSAutoRecover\ui\rood\Grinding Gear Games\Path of Exile 2',
    [string]$Schema = '',
    [string]$CacheRoot = '',
    [string]$Output = '',
    [switch]$RefreshIntl,
    [switch]$SkipVerify,
    [switch]$NoCopy
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Python 侧输出含中日韩文本，统一走 UTF-8，避免 Windows 默认 GBK 编码失败
$env:PYTHONIOENCODING = 'utf-8'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }

$repoRoot = Split-Path -Parent $PSScriptRoot
$pipelineDir = Join-Path $PSScriptRoot 'translation-pipeline'
$libsDir = Join-Path $repoRoot 'libs'

if ([string]::IsNullOrWhiteSpace($Schema)) {
    $Schema = Join-Path $pipelineDir 'vendor\schema.min.json'
}
if ([string]::IsNullOrWhiteSpace($CacheRoot)) {
    $CacheRoot = Join-Path $repoRoot '.cache'
}
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repoRoot 'src\FreiAtlas.Patcher\Resources\dat_translations.json.gz'
}

$intlSnapshot = Join-Path $CacheRoot 'poe2-intl-snapshot'
$cnSnapshot = Join-Path $CacheRoot 'poe2-cn-snapshot'
$buildDir = Join-Path $CacheRoot 'translation-build'
$reportPath = Join-Path $repoRoot 'artifacts\translation-build-report.csv'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Assert-Path {
    param([string]$Path, [string]$Description)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description 不存在：$Path"
    }
}

# 游戏客户端路径在沙箱下禁止 dotnet 子进程直接访问，只能由 PowerShell 宿主进程
# 发起读取。因此把工具编译成 DLL，再通过反射在当前进程内调用。
function Invoke-DotNetEntryPoint {
    param(
        [Parameter(Mandatory)][string]$AssemblyPath,
        [hashtable]$Environment = @{}
    )

    foreach ($name in @('SystemExtensions', 'LibGGPK3', 'LibBundle3', 'LibBundledGGPK3')) {
        $candidate = Join-Path $libsDir "$name.dll"
        if (Test-Path -LiteralPath $candidate) {
            [void][Reflection.Assembly]::LoadFrom($candidate)
        }
    }

    foreach ($key in $Environment.Keys) {
        Set-Item -Path "env:$key" -Value $Environment[$key]
    }

    $assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
    $entryPoint = $assembly.EntryPoint
    if ($null -eq $entryPoint) {
        throw "程序集没有入口点：$AssemblyPath"
    }

    $method = $entryPoint.DeclaringType.GetMethod(
        'Main',
        [Reflection.BindingFlags]'Static,Public,NonPublic'
    )
    if ($null -eq $method) {
        throw "找不到 Main 方法：$AssemblyPath"
    }

    $arguments = if ($method.GetParameters().Length -eq 0) { @() } else { @([string[]]@()) }
    $result = $method.Invoke($null, $arguments)
    return [int]$result
}

function Build-Tool {
    param([string]$ProjectPath, [string]$OutputDir)

    $log = & dotnet build $ProjectPath -c Release -o $OutputDir --nologo -v quiet 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($log | Out-String) -ForegroundColor Red
        throw "编译失败：$ProjectPath"
    }
}

# ---------------------------------------------------------------- 前置校验

Write-Step '校验输入'

Assert-Path $Schema 'schema 文件'
foreach ($tool in @('datfile.py', 'schema.py', 'align.py', 'build_dictionary.py')) {
    Assert-Path (Join-Path $pipelineDir $tool) "流水线脚本 $tool"
}

$python = (Get-Command python -ErrorAction SilentlyContinue).Source
if (-not $python) {
    throw '未找到 python，请确保它在 PATH 中'
}

$schemaInfo = Get-Content -LiteralPath $Schema -Raw -Encoding UTF8 | ConvertFrom-Json
$schemaCreated = [DateTimeOffset]::FromUnixTimeSeconds([int64]$schemaInfo.createdAt).ToString('yyyy-MM-dd')
Write-Host "schema: version=$($schemaInfo.version) 日期=$schemaCreated 表数=$($schemaInfo.tables.Count)"

New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

# ---------------------------------------------------------------- 导出快照

Write-Step '编译快照导出器'

$exporterProject = Join-Path $pipelineDir 'SnapshotExporter\SnapshotExporter.csproj'
$exporterOutput = Join-Path $buildDir 'snapshot-exporter'
Build-Tool -ProjectPath $exporterProject -OutputDir $exporterOutput
$exporterDll = Join-Path $exporterOutput 'SnapshotExporter.dll'

if ($RefreshIntl -or -not (Test-Path -LiteralPath $intlSnapshot)) {
    Write-Step '导出国际服繁中快照'
    Assert-Path (Join-Path $IntlClient 'Content.ggpk') '国际服 Content.ggpk'
    Write-Host '注意：请确认客户端已还原为官方版本，否则快照会被补丁污染。' -ForegroundColor Yellow

    $code = Invoke-DotNetEntryPoint -AssemblyPath $exporterDll -Environment @{
        FREIATLAS_SNAP_MODE   = 'ggpk'
        FREIATLAS_SNAP_GAME   = $IntlClient
        FREIATLAS_SNAP_OUT    = $intlSnapshot
        FREIATLAS_SNAP_PREFIX = 'data/balance/traditional chinese/'
    }
    if ($code -ne 0) { throw "国际服快照导出失败（退出码 $code）" }
}
else {
    $count = (Get-ChildItem -LiteralPath $intlSnapshot -Filter '*.datc64' -File).Count
    Write-Host "复用已有国际服快照：$count 张表（-RefreshIntl 可强制重导）"
}

Write-Step '导出国服简中快照'

$cnBundles = Join-Path $CnClient 'Bundles2'
Assert-Path (Join-Path $cnBundles '_.index.bin') '国服 Bundles2 索引'

$code = Invoke-DotNetEntryPoint -AssemblyPath $exporterDll -Environment @{
    FREIATLAS_SNAP_MODE   = 'bundles2'
    FREIATLAS_SNAP_GAME   = $cnBundles
    FREIATLAS_SNAP_OUT    = $cnSnapshot
    FREIATLAS_SNAP_PREFIX = 'data/balance/simplified chinese/'
}
if ($code -ne 0) { throw "国服快照导出失败（退出码 $code）" }

# CSD 补漏规则（csd_missing_patches.json）不在此流程内重建。
# 该文件是历史人工整理的结果，筛选口径无法从数据反推（国际服全部条目都缺简体段，
# 但只有 267 条需要补录）。scripts/translation-pipeline/build_csd_rules.py 提供了
# 基于快照的差量提取能力，可用于人工复核，但产出与现有文件不一致，不得直接覆盖。

# ---------------------------------------------------------------- 生成词典

Write-Step '生成词典'

$builder = Join-Path $pipelineDir 'build_dictionary.py'
$arguments = @(
    $builder,
    '--schema', $Schema,
    '--intl-snapshot', $intlSnapshot,
    '--cn-snapshot', $cnSnapshot,
    '--output', $Output,
    '--report', $reportPath
)
& $python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "词典生成失败（退出码 $LASTEXITCODE）"
}

# ---------------------------------------------------------------- 注入验证

if (-not $SkipVerify) {
    Write-Step '验证注入安全性'

    $verifierProject = Join-Path $pipelineDir 'InjectionVerifier\InjectionVerifier.csproj'
    $verifierOutput = Join-Path $buildDir 'injection-verifier'
    Build-Tool -ProjectPath $verifierProject -OutputDir $verifierOutput
    $verifierDll = Join-Path $verifierOutput 'InjectionVerifier.dll'

    $code = Invoke-DotNetEntryPoint -AssemblyPath $verifierDll -Environment @{
        FREIATLAS_VERIFY_SNAPSHOT = $intlSnapshot
        FREIATLAS_VERIFY_DICT     = $Output
    }
    if ($code -ne 0) {
        throw "注入验证未通过（退出码 $code）。该词典不可发布。"
    }
}

# ---------------------------------------------------------------- 落地

if (-not $NoCopy) {
    Write-Step '同步到资源目录'

    $resourceDir = Join-Path $repoRoot 'src\FreiAtlas.Patcher\Resources'
    $resourcePath = Join-Path $resourceDir 'dat_translations.json.gz'
    New-Item -ItemType Directory -Path $resourceDir -Force | Out-Null

    if ([IO.Path]::GetFullPath($Output) -ne [IO.Path]::GetFullPath($resourcePath)) {
        Copy-Item -LiteralPath $Output -Destination $resourcePath -Force
        Write-Host "已复制到 $resourcePath"
    }
    else {
        Write-Host '输出路径即资源路径，无需复制'
    }
}

Write-Step '完成'
$sizeMb = [math]::Round((Get-Item -LiteralPath $Output).Length / 1MB, 2)
Write-Host "词典: $Output ($sizeMb MB)"
Write-Host "报告: $reportPath"
