[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Path
)

$ErrorActionPreference = 'Stop'

$resolvedPath = (Resolve-Path -LiteralPath $Path).Path
$document = Get-Content -Raw -LiteralPath $resolvedPath | ConvertFrom-Json
$frameworks = @($document.runtimeOptions.frameworks)

$desktopFrameworks = @(
    $frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' }
)
$coreFrameworks = @(
    $frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }
)

if ($desktopFrameworks.Count -ne 1 -or $coreFrameworks.Count -ne 1) {
    throw "Expected exactly one Microsoft.WindowsDesktop.App and one Microsoft.NETCore.App framework in '$resolvedPath'."
}

$remainingFrameworks = @(
    $frameworks | Where-Object { $_.name -ne 'Microsoft.WindowsDesktop.App' }
)
$document.runtimeOptions.frameworks = @($desktopFrameworks[0]) + $remainingFrameworks

$json = $document | ConvertTo-Json -Depth 100
$utf8WithoutBom = [System.Text.UTF8Encoding]::new($false)
[System.IO.File]::WriteAllText($resolvedPath, $json + [Environment]::NewLine, $utf8WithoutBom)

Write-Output "Prepared runtime config: $resolvedPath"
