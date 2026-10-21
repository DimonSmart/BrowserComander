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


$extensionRoot = $manifestFile.Directory.FullName
$manifest = Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json
if ([string]$manifest.version -ne $Version) { throw "Extension manifest version mismatch." }

function Assert-ExtensionAsset {
    param([Parameter(Mandatory)][string]$RelativePath)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.Contains('*')) { return }
    if ($RelativePath -match '^(?:https?:|data:|chrome:|//|#)') { return }
    $relative = ($RelativePath -split '[?#]', 2)[0].TrimStart('/')
    if ($relative -eq '' -or $relative -eq '.') { return } # HTML <base href="/"> is a directory, not an asset.
    $target = [System.IO.Path]::GetFullPath((Join-Path $extensionRoot $relative))
    $prefix = $extensionRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $target.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid extension asset path: $RelativePath"
    }
    Assert-FileExists -Path $target -Description 'Referenced extension asset'
}

if ($manifest.background.service_worker -ne 'background-agent.js' -or $manifest.background.type -ne 'module') {
    throw 'Generated MV3 manifest does not preserve background-agent.js module worker.'
}
if ($manifest.content_security_policy.extension_pages -ne "script-src 'self' 'wasm-unsafe-eval'; object-src 'self'") {
    throw 'Generated MV3 manifest has unexpected extension CSP.'
}
foreach ($asset in @($manifest.background.service_worker, $manifest.action.default_popup, $manifest.options_ui.page)) {
    Assert-ExtensionAsset -RelativePath $asset
}
foreach ($icon in $manifest.icons.PSObject.Properties) {
    Assert-ExtensionAsset -RelativePath ([string]$icon.Value)
}
foreach ($scriptGroup in @($manifest.content_scripts)) {
    foreach ($property in @('js', 'css')) {
        $entries = $scriptGroup.PSObject.Properties[$property]
        if ($null -eq $entries) { continue }
        foreach ($asset in @($entries.Value)) {
            if ($null -ne $asset) { Assert-ExtensionAsset -RelativePath $asset }
        }
    }
}
foreach ($pageName in @($manifest.action.default_popup, $manifest.options_ui.page)) {
    $html = Get-Content -LiteralPath (Join-Path $extensionRoot $pageName) -Raw
    foreach ($match in [regex]::Matches($html, '(?i)(?:src|href)\s*=\s*["'']([^"'']+)["'']')) {
        Assert-ExtensionAsset -RelativePath $match.Groups[1].Value
    }
}
$wasmFiles = @(Get-ChildItem -LiteralPath $extensionRoot -Recurse -File -Filter '*.wasm')
if ($wasmFiles.Count -eq 0) { throw 'Extracted extension has no WebAssembly runtime.' }
Write-Host "Extracted manifest, popup/options references and WASM assets validated: $extensionRoot"

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


# The release gate tests the extracted ZIP, not the extension's build output.
$testProject = Join-Path $repoRoot 'BrowserCommander.E2E.Tests/BrowserCommander.E2E.Tests.csproj'
$env:BROWSER_COMMANDER_EXTENSION_PATH = $extensionRoot
$env:BROWSER_COMMANDER_RUN_E2E = '1'
$env:BROWSER_COMMANDER_E2E_HEADLESS = '1'
try {
    & dotnet test $testProject -c Release --no-build --filter 'FullyQualifiedName~BlazorExtensionUiE2ETests' --logger 'trx;LogFileName=release-ui.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Release Blazor UI E2E smoke tests failed.' }

    $testResultsDir = Join-Path $repoRoot 'BrowserCommander.E2E.Tests/TestResults'
    $trxPaths = @(Get-ChildItem -Path $testResultsDir -Recurse -File -Filter 'release-ui.trx')
    if ($trxPaths.Count -ne 1) { throw "Expected one release-ui.trx, got $($trxPaths.Count)." }
    [xml]$trx = Get-Content -LiteralPath $trxPaths[0].FullName -Raw
    $cases = @($trx.SelectNodes("//*[local-name()='UnitTestResult']"))
    $requiredCases = @($cases | Where-Object { $_.testName -match 'BlazorExtensionUiE2ETests' })
    $nonPassing = @($requiredCases | Where-Object { $_.outcome -ne 'Passed' })
    if ($requiredCases.Count -ne 3 -or $nonPassing.Count -gt 0) {
        throw "Expected three passed release Blazor UI tests, got $($requiredCases.Count) and $($nonPassing.Count) nonpassing."
    }
}
finally {
    Remove-Item Env:BROWSER_COMMANDER_EXTENSION_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:BROWSER_COMMANDER_RUN_E2E -ErrorAction SilentlyContinue
    Remove-Item Env:BROWSER_COMMANDER_E2E_HEADLESS -ErrorAction SilentlyContinue
}

& (Join-Path $PSScriptRoot 'Smoke-PortableRelease.ps1') -PortableRoot $portableRoot

Write-Host "Release bundle validation passed for version $Version."
Write-Host " - $extensionZipPath"
Write-Host " - $portableZipPath"
Write-Host " - $checksumPath"
