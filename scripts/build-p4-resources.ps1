# 从源码提取 ColorMod / MtxTranslation 数据，生成内嵌资源，并与黄金快照比对。
# 仅用于 P4 迁移前的一次性提取；迁移后源码内联表已移除，脚本会因提取为空而拒绝覆盖资源。
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -AssemblyName System.IO.Compression

function Write-GzJson($obj, $path) {
    $json = $obj | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $ms = New-Object IO.MemoryStream
    $gz = New-Object IO.Compression.GZipStream($ms, [IO.Compression.CompressionMode]::Compress)
    $gz.Write($bytes, 0, $bytes.Length); $gz.Dispose()
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    [IO.File]::WriteAllBytes($path, $ms.ToArray())
    $ms.Dispose()
}

# ---------- ColorMod ----------
$cmPath = Join-Path $root 'src\FreiAtlas.Patcher\Modules\ColorMod\ColorModDefaults.cs'
$lines = [IO.File]::ReadAllLines($cmPath)
$colors = [ordered]@{}
$styles = [ordered]@{}
foreach ($line in $lines) {
    if ($line -match '^\s*dictionary\["([^"]+)"\]\s*=\s*new byte\[3\]\s*\{\s*(\d+),\s*(\d+),\s*(\d+)\s*\};') {
        $colors[$matches[1]] = @([int]$matches[2], [int]$matches[3], [int]$matches[4])
    }
    elseif ($line -match '^\s*\["([^"]+)"\]\s*=\s*ColorModStyle\.(\w+),?\s*$') {
        $styles[$matches[1]] = $matches[2]
    }
}
Write-Output "ColorMod 提取: 颜色 $($colors.Count)  样式 $($styles.Count)"
if ($colors.Count -eq 0 -or $styles.Count -eq 0) {
    throw '未从 ColorModDefaults.cs 提取到数据；源码表已迁移到 Resources\colormod\defaults.json.gz，禁止重跑本脚本覆盖资源。'
}

$colorArr = @()
foreach ($k in $colors.Keys) { $colorArr += [ordered]@{ id = $k; rgb = $colors[$k] } }
$styleArr = @()
foreach ($k in $styles.Keys) { $styleArr += [ordered]@{ id = $k; style = $styles[$k] } }

Write-GzJson @{ version = 1; colors = $colorArr; styles = $styleArr } (Join-Path $root 'src\FreiAtlas.Patcher\Resources\colormod\defaults.json.gz')

# ---------- MtxTranslation ----------
$mtxPath = Join-Path $root 'src\FreiAtlas.Patcher\MtxPatch\MtxTranslationDictionary.cs'
$mlines = [IO.File]::ReadAllLines($mtxPath)
$exact = [ordered]@{}
$phrases = @()
foreach ($line in $mlines) {
    if ($line -match '^\s*\["([^"]+)"\]\s*=\s*"([^"]*)",\s*$') { $exact[$matches[1]] = $matches[2] }
    elseif ($line -match '^\s*\("([^"]*)",\s*"([^"]*)"\),\s*$') { $phrases += ,@($matches[1], $matches[2]) }
}
Write-Output "Mtx 提取: exact $($exact.Count)  phrases $($phrases.Count)"
if ($exact.Count -eq 0 -or $phrases.Count -eq 0) {
    throw '未从 MtxTranslationDictionary.cs 提取到数据；源码表已迁移到 Resources\mtx\translations.json.gz，禁止重跑本脚本覆盖资源。'
}

$exactArr = @()
foreach ($k in $exact.Keys) { $exactArr += [ordered]@{ en = $k; zh = $exact[$k] } }
$phraseArr = @()
for ($i = 0; $i -lt $phrases.Count; $i++) { $phraseArr += [ordered]@{ index = $i; pattern = $phrases[$i][0]; zh = $phrases[$i][1] } }

Write-GzJson @{ version = 1; exact = $exactArr; phrases = $phraseArr } (Join-Path $root 'src\FreiAtlas.Patcher\Resources\mtx\translations.json.gz')

# ---------- 与黄金快照比对 ----------
$td = Join-Path $root 'tests\FreiAtlas.Patcher.Tests\TestData'
$gColors = Get-Content (Join-Path $td 'colormod.colors.golden.json') -Raw | ConvertFrom-Json
$gStyles = Get-Content (Join-Path $td 'colormod.styles.golden.json') -Raw | ConvertFrom-Json
$gExact  = Get-Content (Join-Path $td 'mtx.exact.golden.json') -Raw | ConvertFrom-Json
$gPhrase = Get-Content (Join-Path $td 'mtx.phrases.golden.json') -Raw | ConvertFrom-Json

$ok = $true
if ($colorArr.Count -ne $gColors.Count) { $ok = $false; Write-Output "✗ 颜色数量 $($colorArr.Count) vs $($gColors.Count)" }
if ($styleArr.Count -ne $gStyles.Count) { $ok = $false; Write-Output "✗ 样式数量" }
if ($exactArr.Count -ne $gExact.Count) { $ok = $false; Write-Output "✗ exact 数量" }
if ($phraseArr.Count -ne $gPhrase.Count) { $ok = $false; Write-Output "✗ phrases 数量" }

# 逐条比对颜色值
for ($i = 0; $i -lt $colorArr.Count; $i++) {
    if ($colorArr[$i].id -ne $gColors[$i].id -or ($colorArr[$i].rgb -join ',') -ne ($gColors[$i].rgb -join ',')) {
        $ok = $false; Write-Output "✗ 颜色第 $i 条不一致: $($colorArr[$i].id)"; break
    }
}
# 逐条比对短语顺序
for ($i = 0; $i -lt $phraseArr.Count; $i++) {
    if ($phraseArr[$i].pattern -ne $gPhrase[$i].pattern -or $phraseArr[$i].zh -ne $gPhrase[$i].zh) {
        $ok = $false; Write-Output "✗ 短语第 $i 条不一致"; break
    }
}
Write-Output ($(if ($ok) { "✓ 与黄金快照完全一致" } else { "✗ 存在差异" }))
