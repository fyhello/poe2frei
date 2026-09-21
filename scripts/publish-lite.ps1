[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string] $ReleaseName = 'FreiAtlas-2026.08.02-win-x64-r11-lite-zh-runtime-check'
)

$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$settingsProjectPath = Join-Path $workspaceRoot 'src\FreiAtlas.Settings\FreiAtlas.Settings.csproj'
$launcherProjectPath = Join-Path $workspaceRoot 'src\FreiAtlas.Launcher\FreiAtlas.Launcher.csproj'
$stagePath = Join-Path $workspaceRoot "artifacts\release-stage\$ReleaseName"
$managedStagePath = Join-Path $stagePath 'app'
$zipPath = Join-Path $workspaceRoot "artifacts\releases\$ReleaseName.zip"

if (Test-Path -LiteralPath $stagePath) {
    throw "Release stage already exists: $stagePath"
}

if (Test-Path -LiteralPath $zipPath) {
    throw "Release ZIP already exists: $zipPath"
}

New-Item -ItemType Directory -Path (Split-Path -Parent $stagePath) -Force | Out-Null
New-Item -ItemType Directory -Path (Split-Path -Parent $zipPath) -Force | Out-Null

& dotnet publish $settingsProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained false `
    --no-restore `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $managedStagePath
if ($LASTEXITCODE -ne 0) {
    throw "Settings publish failed with exit code $LASTEXITCODE."
}

$runtimeConfigPath = Join-Path $managedStagePath 'FreiAtlas.Settings.runtimeconfig.json'
& (Join-Path $PSScriptRoot 'prepare-lite-runtimeconfig.ps1') -Path $runtimeConfigPath

$runtimeConfig = Get-Content -Raw -LiteralPath $runtimeConfigPath | ConvertFrom-Json
$frameworkNames = @($runtimeConfig.runtimeOptions.frameworks | ForEach-Object { $_.name })
if ($frameworkNames.Count -lt 2 -or
    $frameworkNames[0] -ne 'Microsoft.WindowsDesktop.App' -or
    $frameworkNames[1] -ne 'Microsoft.NETCore.App') {
    throw "Runtime config does not prioritize Microsoft.WindowsDesktop.App: $($frameworkNames -join ', ')"
}

& dotnet publish $launcherProjectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    --no-restore `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $stagePath
if ($LASTEXITCODE -ne 0) {
    throw "Launcher publish failed with exit code $LASTEXITCODE."
}

Copy-Item `
    -LiteralPath (Join-Path $managedStagePath 'WebView2Loader.dll') `
    -Destination (Join-Path $stagePath 'WebView2Loader.dll')

$requirementsPath = Join-Path $stagePath 'RUNTIME-REQUIREMENTS.txt'
$requirementsSource = Join-Path $workspaceRoot 'docs\release\RUNTIME-REQUIREMENTS.txt'
Copy-Item -LiteralPath $requirementsSource -Destination $requirementsPath

$requiredFiles = @(
    'FreiAtlas.exe',
    'WebView2Loader.dll',
    'RUNTIME-REQUIREMENTS.txt',
    'app\FreiAtlas.Settings.exe',
    'app\FreiAtlas.Settings.dll',
    'app\FreiAtlas.Settings.deps.json',
    'app\FreiAtlas.Settings.runtimeconfig.json',
    'app\THIRD-PARTY-NOTICES.md',
    'app\licenses\POE2Radar-MIT.txt',
    'app\Web\index.html',
    'app\Web\app.js',
    'app\Web\app.css',
    'app\runtimes\win-x64\native\WebView2Loader.dll'
)

foreach ($relativePath in $requiredFiles) {
    $fullPath = Join-Path $stagePath $relativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Required release file is missing: $relativePath"
    }
}

if (Test-Path -LiteralPath (Join-Path $stagePath 'FreiAtlas.Settings.exe')) {
    throw 'Managed settings executable must remain inside the app directory.'
}

$forbiddenRuntimeFiles = @(
    'coreclr.dll',
    'clrjit.dll',
    'hostfxr.dll',
    'hostpolicy.dll',
    'System.Private.CoreLib.dll',
    'PresentationFramework.dll'
)
$forbiddenFiles = @(
    Get-ChildItem -LiteralPath $stagePath -Recurse -File |
        Where-Object {
            $_.Extension -eq '.pdb' -or
            $_.Name -eq 'settings.json' -or
            $forbiddenRuntimeFiles -contains $_.Name
        }
)
$forbiddenDirectories = @(
    Get-ChildItem -LiteralPath $stagePath -Recurse -Directory |
        Where-Object { $_.Name -eq 'EBWebView' -or $_.Name -eq 'TestResults' }
)

if ($forbiddenFiles.Count -gt 0 -or $forbiddenDirectories.Count -gt 0) {
    $forbiddenPaths = @($forbiddenFiles.FullName) + @($forbiddenDirectories.FullName)
    throw "Forbidden release content found:`n$($forbiddenPaths -join [Environment]::NewLine)"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $stagePath,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Optimal,
    $false)

$sourceEntries = @(
    Get-ChildItem -LiteralPath $stagePath -Recurse -File |
        ForEach-Object {
            $_.FullName.Substring($stagePath.Length + 1).Replace('\', '/')
        } |
        Sort-Object
)

$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $archiveEntries = @(
        $archive.Entries |
            Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object { $_.FullName.Replace('\', '/') } |
            Sort-Object
    )
}
finally {
    $archive.Dispose()
}

if (Compare-Object -ReferenceObject $sourceEntries -DifferenceObject $archiveEntries) {
    throw 'ZIP file list does not match the release stage.'
}

$zipFile = Get-Item -LiteralPath $zipPath
$hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
Write-Output "Release ZIP: $($zipFile.FullName)"
Write-Output "ZIP bytes: $($zipFile.Length)"
Write-Output "ZIP entries: $($archiveEntries.Count)"
Write-Output "SHA-256: $($hash.Hash)"
