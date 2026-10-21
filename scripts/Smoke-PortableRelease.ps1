[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PortableRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) { throw 'Portable release executable smoke requires Windows.' }

$serverPath = Join-Path $PortableRoot 'BrowserCommanderServer.exe'
$bridgePath = Join-Path $PortableRoot 'BrowserCommander.McpStdioBridge.exe'
foreach ($file in @($serverPath, $bridgePath)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing executable: $file" }
    if ((Get-Item -LiteralPath $file).Length -lt 1MB) {
        throw "Executable appears not to contain a self-contained single-file runtime: $file"
    }
}

$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
$listener.Stop()
$baseUrl = "http://127.0.0.1:$port"
$server = $null
$bridge = $null

function Read-McpResponse {
    param([System.Diagnostics.Process]$Process, [int]$ExpectedId)
    for ($i = 0; $i -lt 8; $i++) {
        $read = $Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait(20000)) { throw "Timed out reading MCP response for request $ExpectedId" }
        if ($null -eq $read.Result) { throw "MCP bridge ended stdout before response $ExpectedId" }
        $line = [string]$read.Result
        try { $message = $line | ConvertFrom-Json -ErrorAction Stop }
        catch { throw "MCP bridge emitted non-JSON text on stdout: $line" }
        if ($null -ne $message.id -and $message.id -eq $ExpectedId) {
            if ($null -ne $message.error) { throw "MCP request $ExpectedId failed: $line" }
            return $message
        }
    }
    throw "No MCP result received for request $ExpectedId"
}

try {
    $serverInfo = [System.Diagnostics.ProcessStartInfo]::new($serverPath)
    $serverInfo.WorkingDirectory = $PortableRoot
    $serverInfo.UseShellExecute = $false
    $serverInfo.RedirectStandardOutput = $true
    $serverInfo.RedirectStandardError = $true
    $serverInfo.Environment['ASPNETCORE_URLS'] = $baseUrl
    $serverInfo.Environment['DOTNET_ROOT'] = Join-Path $PortableRoot '_missing-dotnet-runtime'
    $serverInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $server = [System.Diagnostics.Process]::Start($serverInfo)

    $ready = $false
    for ($i = 0; $i -lt 120; $i++) {
        if ($server.HasExited) { throw "Published server exited with code $($server.ExitCode)" }
        try {
            $identity = Invoke-WebRequest -Uri "$baseUrl/whoami" -TimeoutSec 2 -UseBasicParsing
            if ($identity.StatusCode -eq 200 -and
                $identity.Headers['X-BrowserCommander-Service'] -eq 'BrowserCommanderServer') {
                $ready = $true
                break
            }
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw 'Published server did not serve /whoami in 30 seconds.' }
    $health = Invoke-WebRequest -Uri "$baseUrl/health" -TimeoutSec 5 -UseBasicParsing
    if ($health.StatusCode -ne 200) { throw 'Published server /health failed.' }

    $bridgeInfo = [System.Diagnostics.ProcessStartInfo]::new($bridgePath)
    $bridgeInfo.WorkingDirectory = $PortableRoot
    $bridgeInfo.UseShellExecute = $false
    $bridgeInfo.RedirectStandardInput = $true
    $bridgeInfo.RedirectStandardOutput = $true
    $bridgeInfo.RedirectStandardError = $true
    $bridgeInfo.ArgumentList.Add("$baseUrl/mcp")
    $bridgeInfo.Environment['DOTNET_ROOT'] = Join-Path $PortableRoot '_missing-dotnet-runtime'
    $bridgeInfo.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
    $bridge = [System.Diagnostics.Process]::Start($bridgeInfo)

    $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"release-smoke","version":"1.0"}}}')
    $bridge.StandardInput.Flush()
    $init = Read-McpResponse -Process $bridge -ExpectedId 1
    if ($null -eq $init.result.serverInfo) { throw 'Stdio initialize did not return serverInfo.' }

    $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
    $bridge.StandardInput.Flush()
    $list = Read-McpResponse -Process $bridge -ExpectedId 2
    if ('browser_list_pages' -notin @($list.result.tools | ForEach-Object { $_.name })) {
        throw 'Stdio MCP tools/list did not contain browser_list_pages.'
    }

    $bridge.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"browser_list_viewport_presets","arguments":{}}}')
    $bridge.StandardInput.Flush()
    $call = Read-McpResponse -Process $bridge -ExpectedId 3
    if ($null -eq $call.result -or $call.result.isError -eq $true) {
        throw 'Stdio MCP functional tools/call failed.'
    }
    Write-Host 'Published server (/whoami, /health, /mcp) and stdio bridge (initialize, tools/list, tools/call) smoke passed.'
    Write-Host 'Runtime isolation: DOTNET_ROOT points to an absent directory; no global runtime lookup requested.'
}
finally {
    foreach ($process in @($bridge, $server)) {
        if ($null -eq $process) { continue }
        try {
            if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
        } finally { $process.Dispose() }
    }
}
