# 导出 ColorModDefaults / MtxTranslationDictionary 的当前数据为黄金快照。
# 仅反射读取，不修改任何生产代码。
# 仅适用于 P4 迁移前的源码结构；迁移后 ExactMap / PhraseList 字段已移除。
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dll = Join-Path $root 'src\FreiAtlas.Patcher\bin\Debug\net10.0-windows\FreiAtlas.Patcher.dll'
if (-not (Test-Path $dll)) { throw "未找到 $dll，请先构建 FreiAtlas.Patcher" }

$asm = [System.Reflection.Assembly]::LoadFrom($dll)
$flags = [System.Reflection.BindingFlags]'Public,NonPublic,Static'

# ---- ColorModDefaults ----
$cmType = $asm.GetType('FreiAtlas.Patcher.Modules.ColorMod.ColorModDefaults')
if ($null -eq $cmType) { throw '未找到 ColorModDefaults' }

$colorMap = $cmType.GetField('DefaultColorMap', $flags).GetValue($null)
$styleMap = $cmType.GetField('DefaultStyleMap', $flags).GetValue($null)

$colors = @()
foreach ($k in $colorMap.Keys) {
    $v = $colorMap[$k]
    $colors += [ordered]@{ id = $k; rgb = @([int]$v[0], [int]$v[1], [int]$v[2]) }
}
$styles = @()
foreach ($k in $styleMap.Keys) {
    $styles += [ordered]@{ id = $k; style = $styleMap[$k].ToString() }
}

# ---- MtxTranslationDictionary ----
$mtxType = $asm.GetType('FreiAtlas.Patcher.MtxPatch.MtxTranslationDictionary')
if ($null -eq $mtxType) { throw '未找到 MtxTranslationDictionary' }

$exactMap = $mtxType.GetField('ExactMap', $flags).GetValue($null)
$phraseList = $mtxType.GetField('PhraseList', $flags).GetValue($null)

$exact = @()
foreach ($k in $exactMap.Keys) { $exact += [ordered]@{ en = $k; zh = $exactMap[$k] } }

$phrases = @()
$i = 0
foreach ($p in $phraseList) {
    $phrases += [ordered]@{ index = $i; pattern = $p.Item1; zh = $p.Item2 }
    $i++
}

Write-Output "ColorMod  颜色: $($colors.Count)  样式: $($styles.Count)"
Write-Output "Mtx       exact: $($exact.Count)  phrases: $($phrases.Count)"

$outDir = Join-Path $root 'tests\FreiAtlas.Patcher.Tests\TestData'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$jsonOpts = @{ Depth = 10 }
$colors   | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $outDir 'colormod.colors.golden.json') -Encoding utf8NoBOM
$styles   | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $outDir 'colormod.styles.golden.json') -Encoding utf8NoBOM
$exact    | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $outDir 'mtx.exact.golden.json') -Encoding utf8NoBOM
$phrases  | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $outDir 'mtx.phrases.golden.json') -Encoding utf8NoBOM

Write-Output "已写出到 $outDir"
