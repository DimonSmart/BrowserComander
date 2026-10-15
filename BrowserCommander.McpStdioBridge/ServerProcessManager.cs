using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace BrowserCommander.McpStdioBridge;

public readonly record struct EndpointAvailability(
    bool IsAvailable,
    string? FailureMessage)
{
    public static EndpointAvailability Available() => new(true, null);

    public static EndpointAvailability Unavailable(string failureMessage) =>
        new(false, failureMessage);
}

public sealed class ServerProcessManager : IAsyncDisposable
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);
    private const int MaxCapturedOutputLines = 40;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _capturedOutputLines = new();
    private readonly HttpClient _probeClient;
    private readonly StdioBridgeOptions _options;

    private Process? _ownedProcess;
    private string? _launchLogPath;

    public ServerProcessManager(StdioBridgeOptions options)
    {
        _options = options;
        _probeClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = ProbeTimeout
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public bool CanAutoStart => _options.CanAutoStartLocalServer;

    public async Task<EndpointAvailability> EnsureEndpointAvailableAsync(
        CancellationToken cancellationToken)
    {
        CleanupExitedOwnedProcess();

        if (await IsEndpointReachableAsync(cancellationToken))
        {
            return EndpointAvailability.Available();
        }

        if (await ProbeIdentityAsync(cancellationToken) == false)
            return EndpointAvailability.Unavailable(
                $"wrong_server_endpoint: port for '{_options.UpstreamEndpoint}' is occupied by another HTTP service.");

        if (!CanAutoStart)
        {
            return EndpointAvailability.Unavailable(
                $"Upstream MCP server '{_options.UpstreamEndpoint}' is unavailable. Start the server and try again.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            CleanupExitedOwnedProcess();

            if (await IsEndpointReachableAsync(cancellationToken))
            {
                return EndpointAvailability.Available();
            }

            if (_ownedProcess is null)
            {
                var startResult = TryStartOwnedProcess();
                if (!startResult.IsAvailable)
                {
                    return startResult;
                }
            }

            return await WaitForEndpointAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Uri IdentityEndpoint => new(_options.UpstreamServerBaseAddress.TrimEnd('/') + "/whoami");

    // true = our service, false = a different HTTP service, null = nothing listening.
    private async Task<bool?> ProbeIdentityAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var response = await _probeClient.GetAsync(IdentityEndpoint, timeout.Token);
            if (!response.IsSuccessStatusCode) return false;
            if (!response.Headers.TryGetValues("X-BrowserCommander-Service", out var headers)
                || !headers.Contains("BrowserCommanderServer", StringComparer.Ordinal))
                return false;
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return body.RootElement.TryGetProperty("service", out var service)
                && service.GetString() == "BrowserCommanderServer";
        }
        catch (HttpRequestException) { return null; }
        catch (IOException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (JsonException) { return false; }
    }

    public async Task<bool> IsEndpointReachableAsync(CancellationToken cancellationToken)
    {
        if (await ProbeIdentityAsync(cancellationToken) != true) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        const string initialize = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"browsercommander-bridge-probe","version":"1.0"}}}
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.UpstreamEndpoint)
        {
            Content = new StringContent(initialize, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        try
        {
            using var response = await _probeClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode
                || !response.Headers.TryGetValues("X-BrowserCommander-Service", out var headers)
                || !headers.Contains("BrowserCommanderServer", StringComparer.Ordinal))
                return false;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            return mediaType == "application/json" || mediaType == "text/event-stream";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
        catch (IOException) { return false; }
    }

    // A stdio bridge never owns the shared server lifetime.
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _ownedProcess?.Dispose();
            _ownedProcess = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            _probeClient.Dispose();
        }
    }

    private EndpointAvailability TryStartOwnedProcess()
    {
        if (!string.IsNullOrWhiteSpace(_options.PublishedServerExecutablePath)
            && File.Exists(_options.PublishedServerExecutablePath))
        {
            return TryStartOwnedProcess(
                CreatePublishedServerStartInfo(_options.PublishedServerExecutablePath),
                $"published server executable '{_options.PublishedServerExecutablePath}'");
        }

        if (string.IsNullOrWhiteSpace(_options.LocalServerProjectPath))
        {
            return EndpointAvailability.Unavailable(
                $"Automatic startup is unavailable because neither BrowserCommanderServer.csproj nor a published BrowserCommanderServer executable was found for '{_options.UpstreamEndpoint}'.");
        }

        ClearCapturedOutput();

        return TryStartOwnedProcess(
            CreateProjectStartInfo(),
            $"project '{_options.LocalServerProjectPath}'");
    }

    private EndpointAvailability TryStartOwnedProcess(
        ProcessStartInfo startInfo,
        string startupTargetDescription)
    {
        ClearCapturedOutput();

        try
        {
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) => CaptureOutput(args.Data);
            process.ErrorDataReceived += (_, args) => CaptureOutput(args.Data);

            if (!process.Start())
            {
                process.Dispose();
                return EndpointAvailability.Unavailable(
                    $"Failed to start BrowserCommanderServer from {startupTargetDescription} for '{_options.UpstreamEndpoint}'.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ownedProcess = process;

            return EndpointAvailability.Available();
        }
        catch (Exception ex)
        {
            return EndpointAvailability.Unavailable(
                $"Failed to start BrowserCommanderServer from {startupTargetDescription} for '{_options.UpstreamEndpoint}': {ex.Message}");
        }
    }

    private ProcessStartInfo CreateProjectStartInfo()
    {
        var directory = _options.RepositoryRootPath
            ?? Path.GetDirectoryName(_options.LocalServerProjectPath!)
            ?? Environment.CurrentDirectory;
        return CreateDetachedServerStartInfo("dotnet",
            ["run", "--project", _options.LocalServerProjectPath!, "--no-launch-profile"],
            directory);
    }

    private ProcessStartInfo CreatePublishedServerStartInfo(string executablePath)
    {
        return CreateDetachedServerStartInfo(executablePath, [],
            Path.GetDirectoryName(executablePath) ?? Environment.CurrentDirectory);
    }

    // The launcher exits immediately; its child uses detached stdio and its own logfile.
    // Otherwise closing a bridge closes the server's inherited stdout/stderr pipes.
    private ProcessStartInfo CreateDetachedServerStartInfo(
        string executable, IReadOnlyList<string> args, string directory)
    {
        _launchLogPath = Path.Combine(Path.GetTempPath(),
            $"browsercommander-server-{Environment.ProcessId}-{Guid.NewGuid():N}.log");

        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
            var argumentLine = string.Join(" ", args.Select(arg =>
                arg.Any(char.IsWhiteSpace) ? "\"" + arg.Replace("\"", "\\\"") + "\"" : arg));
            var argumentsPart = args.Count == 0 ? string.Empty : $"-ArgumentList {PsQuote(argumentLine)} ";
            var stdOut = _launchLogPath;
            var stdErr = _launchLogPath + ".err";
            var command =
                $"$p = Start-Process -FilePath {PsQuote(executable)} " +
                $"{argumentsPart}-WorkingDirectory {PsQuote(directory)} " +
                $"-RedirectStandardOutput {PsQuote(stdOut)} -RedirectStandardError {PsQuote(stdErr)} " +
                "-WindowStyle Hidden -PassThru; Write-Output $p.Id";
            startInfo = new ProcessStartInfo("powershell.exe");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(command);
        }
        else
        {
            static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";
            var command = $"nohup {ShQuote(executable)} " +
                string.Join(" ", args.Select(ShQuote)) +
                $" >> {ShQuote(_launchLogPath)} 2>&1 </dev/null & echo $!";
            startInfo = new ProcessStartInfo("/bin/sh");
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        startInfo.WorkingDirectory = directory;
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.Environment["ASPNETCORE_URLS"] = _options.UpstreamServerBaseAddress;
        return startInfo;
    }

    private async Task<EndpointAvailability> WaitForEndpointAsync(
        CancellationToken cancellationToken)
    {
        var timeoutAt = DateTimeOffset.UtcNow + StartupTimeout;
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await IsEndpointReachableAsync(cancellationToken))
            {
                return EndpointAvailability.Available();
            }

            if (_ownedProcess is { HasExited: true } launcher)
            {
                var exitCode = launcher.ExitCode;
                CleanupExitedOwnedProcess();
                if (exitCode != 0)
                    return EndpointAvailability.Unavailable(CreateStartupFailureMessage(
                        $"BrowserCommanderServer launcher exited with code {exitCode}."));
                // A successful detached launcher exits before the server is ready.
            }

            await Task.Delay(ProbeInterval, cancellationToken);
        }

        return EndpointAvailability.Unavailable(
            CreateStartupFailureMessage(
                $"BrowserCommanderServer did not become ready within {StartupTimeout.TotalSeconds:0} seconds."));
    }

    private string CreateStartupFailureMessage(string reason)
    {
        var outputTail = GetCapturedOutputTail();
        if (!string.IsNullOrWhiteSpace(_launchLogPath) && File.Exists(_launchLogPath))
        {
            try
            {
                var logTail = File.ReadLines(_launchLogPath).TakeLast(8);
                outputTail = string.Join(" | ", logTail);
            }
            catch (IOException) { }
        }
        return string.IsNullOrWhiteSpace(outputTail)
            ? $"{reason} Endpoint: {_options.UpstreamEndpoint}"
            : $"{reason} Endpoint: {_options.UpstreamEndpoint}. Recent output: {outputTail}";
    }

    private string? GetCapturedOutputTail()
    {
        var capturedLines = _capturedOutputLines.ToArray();
        if (capturedLines.Length == 0)
        {
            return null;
        }

        return string.Join(" | ", capturedLines.TakeLast(5));
    }

    private void CaptureOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _capturedOutputLines.Enqueue(line.Trim());
        while (_capturedOutputLines.Count > MaxCapturedOutputLines
               && _capturedOutputLines.TryDequeue(out _))
        {
        }
    }

    private void ClearCapturedOutput()
    {
        while (_capturedOutputLines.TryDequeue(out _))
        {
        }
    }

    private void CleanupExitedOwnedProcess()
    {
        if (_ownedProcess is null || !_ownedProcess.HasExited)
        {
            return;
        }

        _ownedProcess.Dispose();
        _ownedProcess = null;
    }

}
