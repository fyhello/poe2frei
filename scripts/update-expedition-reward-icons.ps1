[CmdletBinding()]
param(
    [string]$SourcePage = 'https://poe2db.tw/us/Runeshape_Combinations'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$catalogPath = Join-Path $repoRoot 'reference-analysis\POE2Radar\src\POE2Radar.Core\Game\expedition2_recipes.json'
$assetParent = Join-Path $repoRoot 'src\FreiAtlas.Platform.Windows\Assets'
$outputPath = Join-Path $assetParent 'ExpeditionRewards'
$sourceManifestPath = Join-Path $PSScriptRoot 'expedition-reward-icon-sources.json'

function Assert-WorkspacePath([string]$Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to write outside the repository: $resolved"
    }
}

function Get-Attribute([string]$Attributes, [string]$Name) {
    $match = [regex]::Match(
        $Attributes,
        "\b$([regex]::Escape($Name))=`"(?<value>[^`"]+)`"",
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    return [Net.WebUtility]::HtmlDecode($match.Groups['value'].Value)
}

function Get-PlainText([string]$Html) {
    $withoutTags = [regex]::Replace($Html, '<[^>]+>', '')
    return [Net.WebUtility]::HtmlDecode($withoutTags).Trim()
}

function Get-AbsoluteUri([string]$Href) {
    if ([Uri]::IsWellFormedUriString($Href, [UriKind]::Absolute)) {
        return $Href
    }

    return [Uri]::new([Uri]'https://poe2db.tw/us/', $Href).AbsoluteUri
}

Assert-WorkspacePath $outputPath
Assert-WorkspacePath $sourceManifestPath
if (-not (Test-Path -LiteralPath $catalogPath)) {
    throw "Recipe catalog not found: $catalogPath"
}

$catalog = Get-Content -Raw -LiteralPath $catalogPath | ConvertFrom-Json
$exactRewards = @(
    $catalog.recipes |
        Where-Object { $null -ne $_.reward } |
        ForEach-Object { $_.reward } |
        Sort-Object id -Unique)
$descriptiveRewards = @(
    $catalog.recipes |
        Where-Object {
            $null -eq $_.reward -and
            -not [string]::IsNullOrWhiteSpace($_.description)
        } |
        Select-Object -ExpandProperty description |
        Sort-Object -Unique)
if ($exactRewards.Count -ne 231 -or $descriptiveRewards.Count -ne 29) {
    throw "Catalog counts changed: exact=$($exactRewards.Count), descriptive=$($descriptiveRewards.Count)"
}

$page = Invoke-WebRequest -Uri $SourcePage -UseBasicParsing
$anchors = [regex]::Matches(
    $page.Content,
    '<a\b(?<attributes>[^>]*)>(?<inner>.*?)</a>',
    [Text.RegularExpressions.RegexOptions]::Singleline -bor
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
$anchorsByName = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
$inlineSourcesById = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$hoverById = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($anchor in $anchors) {
    $attributes = $anchor.Groups['attributes'].Value
    $inner = $anchor.Groups['inner'].Value
    $name = Get-PlainText $inner
    $href = Get-Attribute $attributes 'href'
    $hover = Get-Attribute $attributes 'data-hover'
    $image = Get-Attribute $inner 'src'
    if ($name -and $href -and -not $anchorsByName.ContainsKey($name)) {
        $anchorsByName[$name] = [pscustomobject]@{
            Href = Get-AbsoluteUri $href
            Image = $image
            Hover = $hover
        }
    }

    if ($hover -notlike '*BaseItemTypes*') {
        continue
    }

    $decoded = [Uri]::UnescapeDataString(($hover -replace '^\?s=', ''))
    $marker = 'Data\BaseItemTypes/'
    $markerIndex = $decoded.IndexOf($marker, [StringComparison]::Ordinal)
    if ($markerIndex -ge 0) {
        $itemId = $decoded.Substring($markerIndex + $marker.Length)
        $hoverById[$itemId] = $hover
        if ($image) {
            $inlineSourcesById[$itemId] = $image
        }
    }
}

$exactSources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$pageRequests = [Collections.Generic.List[object]]::new()
foreach ($reward in $exactRewards) {
    $sourceUri = $null
    if ($inlineSourcesById.TryGetValue($reward.id, [ref]$sourceUri)) {
        $exactSources[$reward.id] = $sourceUri
        continue
    }

    $anchor = $null
    if (-not $anchorsByName.TryGetValue($reward.name, [ref]$anchor)) {
        throw "Poe2DB reward anchor not found: $($reward.id) ($($reward.name))"
    }

    $hover = if ($hoverById.ContainsKey($reward.id)) {
        $hoverById[$reward.id]
    }
    else {
        $anchor.Hover
    }
    if (-not $hover) {
        throw "Poe2DB reward hover source not found: $($reward.id) ($($reward.name))"
    }
    $hoverUri = if ($hover.StartsWith('?')) {
        'https://poe2db.tw/us/hover' + $hover
    }
    else {
        Get-AbsoluteUri $hover
    }

    $pageRequests.Add([pscustomobject]@{
        Key = $reward.id
        PageUri = $hoverUri
    })
}

$pageResults = @($pageRequests | ForEach-Object -Parallel {
    $response = $null
    for ($attempt = 1; $attempt -le 3 -and $null -eq $response; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $_.PageUri -UseBasicParsing -Headers @{
                Referer = 'https://poe2db.tw/us/Runeshape_Combinations'
                'User-Agent' = 'Mozilla/5.0 FreiAtlas asset updater'
            }
        }
        catch {
            if ($attempt -eq 3) {
                throw "Hover request failed: $($_.PageUri): $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
    $content = if ($response.Content -is [byte[]]) {
        [Text.Encoding]::UTF8.GetString([byte[]]$response.Content)
    }
    else {
        [string]$response.Content
    }
    $matches = [regex]::Matches(
        $content,
        'https://[^"''\s<>]+\.(?:webp|png)',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $source = $matches |
        ForEach-Object { [Net.WebUtility]::HtmlDecode($_.Value) } |
        Where-Object { $_ -match 'cdn\.poe2db\.tw/image/Art/2DItems/' } |
        Select-Object -First 1
    if (-not $source) {
        throw "Official image not found on $($_.PageUri)"
    }

    [pscustomobject]@{
        Key = $_.Key
        Source = $source
    }
} -ThrottleLimit 8)
foreach ($result in $pageResults) {
    $exactSources[$result.Key] = $result.Source
}

$categoryPages = [ordered]@{
    '[Rarity|Unique] Amulet' = 'Amulets'
    '[Rarity|Unique] Belt' = 'Belts'
    '[Rarity|Unique] Body Armour' = 'Body_Armours'
    '[Rarity|Unique] Boots' = 'Boots'
    '[Rarity|Unique] Bow' = 'Bows'
    '[Rarity|Unique] Crossbow' = 'Crossbows'
    '[Rarity|Unique] Focus' = 'Foci'
    '[Rarity|Unique] Gloves' = 'Gloves'
    '[Rarity|Unique] Helmet' = 'Helmets'
    '[Rarity|Unique] Jewellery' = 'Jewellery'
    '[Rarity|Unique] One Hand Mace' = 'One_Hand_Maces'
    '[Rarity|Unique] Quarterstaff' = 'Quarterstaves'
    '[Rarity|Unique] Quiver' = 'Quivers'
    '[Rarity|Unique] Ring' = 'Rings'
    '[Rarity|Unique] Sceptre' = 'Sceptres'
    '[Rarity|Unique] Shield' = 'Shields'
    '[Rarity|Unique] Spear' = 'Spears'
    '[Rarity|Unique] Staff' = 'Staves'
    '[Rarity|Unique] Talisman' = 'Talismans'
    '[Rarity|Unique] Two Hand Mace' = 'Two_Hand_Maces'
    '[Rarity|Unique] Wand' = 'Wands'
    'Rare [Rarity|Unique] Item' = 'Unique_item'
    'Very Rare [Rarity|Unique] item' = 'Unique_item'
}
$categoryRequests = @($categoryPages.GetEnumerator() | ForEach-Object {
    [pscustomobject]@{
        Key = $_.Key
        PageUri = Get-AbsoluteUri $_.Value
    }
})
$categoryResults = @($categoryRequests | ForEach-Object -Parallel {
    $response = $null
    for ($attempt = 1; $attempt -le 3 -and $null -eq $response; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $_.PageUri -UseBasicParsing -Headers @{
                Referer = 'https://poe2db.tw/us/Runeshape_Combinations'
                'User-Agent' = 'Mozilla/5.0 FreiAtlas asset updater'
            }
        }
        catch {
            if ($attempt -eq 3) {
                throw "Category request failed: $($_.PageUri): $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds (250 * $attempt)
        }
    }
    $matches = [regex]::Matches(
        $response.Content,
        '<img\b[^>]*\bsrc="(?<image>[^"]+)"[^>]*>',
        [Text.RegularExpressions.RegexOptions]::Singleline -bor
            [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    $source = $matches |
        ForEach-Object { [Net.WebUtility]::HtmlDecode($_.Groups['image'].Value) } |
        Where-Object {
            $_ -match 'cdn\.poe2db\.tw/image/Art/2DItems/' -and
            $_ -notmatch '/Maps/'
        } |
        Select-Object -First 1
    if (-not $source) {
        throw "Category image not found on $($_.PageUri)"
    }

    [pscustomobject]@{ Key = $_.Key; Source = $source }
} -ThrottleLimit 8)

$descriptiveSources = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
foreach ($result in $categoryResults) {
    $descriptiveSources[$result.Key] = $result.Source
}

$descriptiveItemAliases = [ordered]@{
    '5x Random Currency' = 'Metadata/Items/Currency/CurrencyUpgradeRandomly'
    "Krillson's Bay Key" = 'Metadata/Items/Expedition/Expedition2LogbookSpecial'
    'Uncut Skill Gem' = 'Metadata/Items/Gems/SkillGemUncut20'
    'Uncut Spirit Gem' = 'Metadata/Items/Gems/ReservationGemUncut20'
    'Verisium Pile' = 'Metadata/Items/Currency/CurrencyVerisiumAlloy1'
}
foreach ($entry in $descriptiveItemAliases.GetEnumerator()) {
    $sourceUri = $null
    if (-not $exactSources.TryGetValue($entry.Value, [ref]$sourceUri)) {
        throw "Descriptive alias item missing: $($entry.Key) -> $($entry.Value)"
    }
    $descriptiveSources[$entry.Key] = $sourceUri
}
$descriptiveSources['Uncut Support Gem'] =
    'https://cdn.poe2db.tw/image/Art/2DItems/Gems/UncutSupportGem.webp'

$missingExact = @($exactRewards | Where-Object { -not $exactSources.ContainsKey($_.id) })
$missingDescriptive = @($descriptiveRewards | Where-Object { -not $descriptiveSources.ContainsKey($_) })
if ($missingExact.Count -gt 0 -or $missingDescriptive.Count -gt 0) {
    throw "Source mapping incomplete: exact=$($missingExact.Count), descriptive=$($missingDescriptive.Count)"
}

$allSources = @($exactSources.Values + $descriptiveSources.Values | Sort-Object -Unique)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("FreiAtlas-expedition-icons-" + [Guid]::NewGuid().ToString('N'))
$downloadRoot = Join-Path $temporaryRoot 'converted'
$batchPath = Join-Path $temporaryRoot 'batch.json'
$pythonPath = Join-Path $temporaryRoot 'convert.py'
New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
try {
    $batch = for ($index = 0; $index -lt $allSources.Count; $index++) {
        [pscustomobject]@{
            url = $allSources[$index]
            output = Join-Path $downloadRoot ("{0:D4}.png" -f $index)
        }
    }
    $batch | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $batchPath -Encoding utf8NoBOM
    @'
import concurrent.futures
import io
import json
import pathlib
import sys
import time
import urllib.request

from PIL import Image

batch_path = pathlib.Path(sys.argv[1])
jobs = json.loads(batch_path.read_text(encoding="utf-8"))

def convert(job):
    request = urllib.request.Request(job["url"], headers={
        "User-Agent": "Mozilla/5.0 FreiAtlas asset updater",
        "Referer": "https://poe2db.tw/us/Runeshape_Combinations",
    })
    content = None
    for attempt in range(3):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                content = response.read()
            break
        except Exception as error:
            if attempt == 2:
                raise RuntimeError(f"download failed: {job['url']}: {error}") from error
            time.sleep(0.25 * (attempt + 1))
    with Image.open(io.BytesIO(content)) as image:
        image = image.convert("RGBA")
        image.thumbnail((48, 48), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (48, 48), (0, 0, 0, 0))
        canvas.alpha_composite(image, ((48 - image.width) // 2, (48 - image.height) // 2))
        canvas.save(job["output"], format="PNG", optimize=True)
    return job["url"]

with concurrent.futures.ThreadPoolExecutor(max_workers=12) as executor:
    list(executor.map(convert, jobs))
'@ | Set-Content -LiteralPath $pythonPath -Encoding utf8NoBOM

    & python $pythonPath $batchPath
    if ($LASTEXITCODE -ne 0) {
        throw 'PNG conversion failed. Python 3 with Pillow WebP support is required.'
    }

    $stagePath = Join-Path $assetParent ('.ExpeditionRewards-stage-' + [Guid]::NewGuid().ToString('N'))
    Assert-WorkspacePath $stagePath
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    $sourceToFile = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $allSources.Count; $index++) {
        $converted = Join-Path $downloadRoot ("{0:D4}.png" -f $index)
        $hash = (Get-FileHash -LiteralPath $converted -Algorithm SHA256).Hash.ToLowerInvariant()
        $fileName = $hash.Substring(0, 20) + '.png'
        $destination = Join-Path $stagePath $fileName
        if (-not (Test-Path -LiteralPath $destination)) {
            Copy-Item -LiteralPath $converted -Destination $destination
        }
        $sourceToFile[$allSources[$index]] = $fileName
    }

    $exactManifest = [ordered]@{}
    foreach ($key in @($exactSources.Keys | Sort-Object)) {
        $exactManifest[$key] = $sourceToFile[$exactSources[$key]]
    }
    $descriptiveManifest = [ordered]@{}
    foreach ($key in @($descriptiveSources.Keys | Sort-Object)) {
        $descriptiveManifest[$key] = $sourceToFile[$descriptiveSources[$key]]
    }
    $manifest = [ordered]@{
        exactItems = $exactManifest
        descriptiveRewards = $descriptiveManifest
    }
    $manifest | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $stagePath 'manifest.json') -Encoding utf8NoBOM

    $sources = [ordered]@{
        sourcePage = $SourcePage
        catalog = [IO.Path]::GetRelativePath($repoRoot, $catalogPath)
        conversion = 'Official Poe2DB/GGG WebP artwork centered in a 48x48 transparent PNG canvas'
        exactItems = [ordered]@{}
        descriptiveRewards = [ordered]@{}
    }
    foreach ($key in @($exactSources.Keys | Sort-Object)) {
        $sources.exactItems[$key] = $exactSources[$key]
    }
    foreach ($key in @($descriptiveSources.Keys | Sort-Object)) {
        $sources.descriptiveRewards[$key] = $descriptiveSources[$key]
    }
    $sources | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath $sourceManifestPath -Encoding utf8NoBOM

    $backupPath = $null
    if (Test-Path -LiteralPath $outputPath) {
        $backupPath = Join-Path $assetParent ('.ExpeditionRewards-backup-' + [Guid]::NewGuid().ToString('N'))
        Assert-WorkspacePath $backupPath
        Move-Item -LiteralPath $outputPath -Destination $backupPath
    }
    try {
        Move-Item -LiteralPath $stagePath -Destination $outputPath
        if ($backupPath) {
            Remove-Item -LiteralPath $backupPath -Recurse -Force
        }
    }
    catch {
        if ($backupPath -and -not (Test-Path -LiteralPath $outputPath)) {
            Move-Item -LiteralPath $backupPath -Destination $outputPath
        }
        throw
    }

    $fileCount = @(Get-ChildItem -LiteralPath $outputPath -Filter '*.png').Count
    Write-Host "exact=$($exactManifest.Count) descriptive=$($descriptiveManifest.Count) missing=0 files=$fileCount"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
