param(
    [switch]$Headless
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")

Push-Location $repoRoot
try {
    dotnet build "BrowserCommander.E2E.Tests\BrowserCommander.E2E.Tests.csproj"
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed."
    }

    $playwrightScript = Get-ChildItem -Path "BrowserCommander.E2E.Tests\bin" -Filter "playwright.ps1" -Recurse |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 -ExpandProperty FullName

    if ([string]::IsNullOrWhiteSpace($playwrightScript)) {
        throw "Could not find playwright.ps1 under BrowserCommander.E2E.Tests\\bin. Build output is incomplete."
    }

    & $playwrightScript install chromium
    if ($LASTEXITCODE -ne 0) {
        throw "Playwright browser installation failed."
    }

    $env:BROWSER_COMMANDER_RUN_E2E = "1"
    if ($Headless) {
        $env:BROWSER_COMMANDER_E2E_HEADLESS = "1"
    } else {
        Remove-Item Env:BROWSER_COMMANDER_E2E_HEADLESS -ErrorAction SilentlyContinue
    }

    dotnet test "BrowserCommander.E2E.Tests\BrowserCommander.E2E.Tests.csproj" --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed."
    }
}
finally {
    Pop-Location
}
