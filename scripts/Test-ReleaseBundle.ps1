[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\\artifacts\\release")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-FileExists {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description '$Path' was not found."
    }
}

function Assert-DirectoryExists {
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$Description
    )

    if (!(Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description '$Path' was not found."
    }
}

function Find-FileRecursively {
    param(
        [Parameter(Mandatory)]
        [string]$Root,
        [Parameter(Mandatory)]
        [string]$Name
    )

    return Get-ChildItem -Path $Root -File -Recurse | Where-Object { $_.Name -eq $Name } | Select-Object -First 1
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$resolvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$extensionZipPath = Join-Path $resolvedOutputRoot "browsercommander-extension-unpacked-v$Version.zip"
$portableZipPath = Join-Path $resolvedOutputRoot "browsercommander-windows-x64-portable-v$Version.zip"
$checksumPath = Join-Path $resolvedOutputRoot "SHA256SUMS.txt"
$validationRoot = Join-Path $resolvedOutputRoot "validation"
$extensionExtractDir = Join-Path $validationRoot "extension"
$portableExtractDir = Join-Path $validationRoot "portable"

Assert-FileExists -Path $extensionZipPath -Description "Extension release archive"
Assert-FileExists -Path $portableZipPath -Description "Portable release archive"
Assert-FileExists -Path $checksumPath -Description "Checksum file"

$checksumLines = Get-Content -Path $checksumPath
$checksumsByFileName = @{}

foreach ($line in $checksumLines) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    if ($line -match '^(?<hash>[0-9a-f]{64}) \*(?<name>.+)$') {
        $checksumsByFileName[$matches.name] = $matches.hash
        continue
    }

    throw "Checksum line '$line' does not match the expected '<sha256> *<file>' format."
}

foreach ($archivePath in @($extensionZipPath, $portableZipPath)) {
    $fileName = [System.IO.Path]::GetFileName($archivePath)
    if (!$checksumsByFileName.ContainsKey($fileName)) {
        throw "Checksum file does not contain an entry for '$fileName'."
    }

    $actualHash = (Get-FileHash -Algorithm SHA256 -Path $archivePath).Hash.ToLowerInvariant()
    if ($actualHash -ne $checksumsByFileName[$fileName]) {
        throw "Checksum mismatch for '$fileName'. Expected '$($checksumsByFileName[$fileName])', got '$actualHash'."
    }
}

if (Test-Path -LiteralPath $validationRoot) {
    Remove-Item -LiteralPath $validationRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $extensionExtractDir -Force | Out-Null
New-Item -ItemType Directory -Path $portableExtractDir -Force | Out-Null

Expand-Archive -LiteralPath $extensionZipPath -DestinationPath $extensionExtractDir -Force
Expand-Archive -LiteralPath $portableZipPath -DestinationPath $portableExtractDir -Force

$manifestFile = Find-FileRecursively -Root $extensionExtractDir -Name "manifest.json"
if ($null -eq $manifestFile) {
    throw "The extension archive does not contain manifest.json."
}

$serverExe = Find-FileRecursively -Root $portableExtractDir -Name "BrowserCommanderServer.exe"
$bridgeExe = Find-FileRecursively -Root $portableExtractDir -Name "BrowserCommander.McpStdioBridge.exe"
$bundleReadme = Find-FileRecursively -Root $portableExtractDir -Name "README.txt"

if ($null -eq $serverExe) {
    throw "The portable archive does not contain BrowserCommanderServer.exe."
}

if ($null -eq $bridgeExe) {
    throw "The portable archive does not contain BrowserCommander.McpStdioBridge.exe."
}

if ($null -eq $bundleReadme) {
    throw "The portable archive does not contain README.txt."
}

$portableRoot = $serverExe.Directory.FullName
$configExamplesDir = Join-Path $portableRoot "config-examples"
Assert-DirectoryExists -Path $configExamplesDir -Description "Portable config examples directory"
Assert-FileExists -Path (Join-Path $configExamplesDir "codex-stdio.example.json") -Description "Codex stdio config example"
Assert-FileExists -Path (Join-Path $configExamplesDir "http-mcp.example.txt") -Description "HTTP MCP config example"

Write-Host "Release bundle validation passed for version $Version."
Write-Host " - $extensionZipPath"
Write-Host " - $portableZipPath"
Write-Host " - $checksumPath"
