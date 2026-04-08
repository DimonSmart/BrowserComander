[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\\artifacts\\release")
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    Write-Host "dotnet $($Arguments -join ' ')"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Reset-Directory {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Copy-DirectoryContent {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    New-Item -ItemType Directory -Path $DestinationPath -Force | Out-Null
    Copy-Item -Path (Join-Path $SourcePath '*') -Destination $DestinationPath -Recurse -Force
}

function New-ZipFromDirectory {
    param(
        [Parameter(Mandatory)]
        [string]$SourcePath,
        [Parameter(Mandatory)]
        [string]$DestinationPath
    )

    if (Test-Path $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }

    Compress-Archive -Path $SourcePath -DestinationPath $DestinationPath -CompressionLevel Optimal
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$manifestPath = Join-Path $repoRoot "BrowserCommander\\wwwroot\\manifest.json"
$manifest = Get-Content -Path $manifestPath -Raw | ConvertFrom-Json
$manifestVersion = [string]$manifest.version

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $manifestVersion
}

if ($Version -ne $manifestVersion) {
    throw "Release version '$Version' must match BrowserCommander/wwwroot/manifest.json version '$manifestVersion'."
}

$resolvedOutputRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputRoot))
$stagingRoot = Join-Path $resolvedOutputRoot "staging"
$publishRoot = Join-Path $stagingRoot "publish"
$serverPublishDir = Join-Path $publishRoot "server"
$bridgePublishDir = Join-Path $publishRoot "bridge"
$extensionBuildDir = Join-Path $repoRoot "BrowserCommander\\bin\\$Configuration\\net8.0\\browserextension"
$extensionPackageDir = Join-Path $stagingRoot "browsercommander-extension"
$portableFolderName = "browsercommander-windows-x64-portable-v$Version"
$portableDir = Join-Path $stagingRoot $portableFolderName
$extensionZipPath = Join-Path $resolvedOutputRoot "browsercommander-extension-unpacked-v$Version.zip"
$portableZipPath = Join-Path $resolvedOutputRoot "$portableFolderName.zip"
$checksumPath = Join-Path $resolvedOutputRoot "SHA256SUMS.txt"
$bundleTemplateDir = Join-Path $repoRoot "distribution\\bundle"

Reset-Directory -Path $resolvedOutputRoot
Reset-Directory -Path $stagingRoot
Reset-Directory -Path $publishRoot

Invoke-Dotnet -Arguments @("build", "BrowserCommander\\BrowserCommander.csproj", "-c", $Configuration)

if (!(Test-Path $extensionBuildDir)) {
    throw "Extension build output '$extensionBuildDir' was not found."
}

Invoke-Dotnet -Arguments @(
    "publish", "BrowserCommanderServer\\BrowserCommanderServer.csproj",
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "/p:PublishSingleFile=true",
    "-o", $serverPublishDir
)

Invoke-Dotnet -Arguments @(
    "publish", "BrowserCommander.McpStdioBridge\\BrowserCommander.McpStdioBridge.csproj",
    "-c", $Configuration,
    "-r", $RuntimeIdentifier,
    "--self-contained", "true",
    "/p:PublishSingleFile=true",
    "-o", $bridgePublishDir
)

Copy-DirectoryContent -SourcePath $extensionBuildDir -DestinationPath $extensionPackageDir
Copy-DirectoryContent -SourcePath $serverPublishDir -DestinationPath $portableDir

foreach ($bridgeFileName in @(
    "BrowserCommander.McpStdioBridge.exe",
    "BrowserCommander.McpStdioBridge.pdb"
)) {
    $bridgeFilePath = Join-Path $bridgePublishDir $bridgeFileName
    if (Test-Path $bridgeFilePath) {
        Copy-Item -LiteralPath $bridgeFilePath -Destination (Join-Path $portableDir $bridgeFileName) -Force
    }
}

Copy-DirectoryContent -SourcePath $bundleTemplateDir -DestinationPath $portableDir

New-ZipFromDirectory -SourcePath $extensionPackageDir -DestinationPath $extensionZipPath
New-ZipFromDirectory -SourcePath $portableDir -DestinationPath $portableZipPath

$hashLines = Get-FileHash -Algorithm SHA256 -Path @(
    $extensionZipPath,
    $portableZipPath
) | ForEach-Object {
    "{0} *{1}" -f $_.Hash.ToLowerInvariant(), [System.IO.Path]::GetFileName($_.Path)
}

Set-Content -Path $checksumPath -Value $hashLines -Encoding UTF8

Write-Host "Release assets created:"
Write-Host " - $extensionZipPath"
Write-Host " - $portableZipPath"
Write-Host " - $checksumPath"
