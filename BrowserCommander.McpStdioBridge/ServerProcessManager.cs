using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

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
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private const int MaxCapturedOutputLines = 40;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<string> _capturedOutputLines = new();
    private readonly HttpClient _probeClient;
    private readonly StdioBridgeOptions _options;

    private Process? _ownedProcess;

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

    public async Task<bool> IsEndpointReachableAsync(CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.UpstreamEndpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };

        try
        {
            using var _ = await _probeClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);

            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopOwnedProcessAsync();
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
        var workingDirectory = _options.RepositoryRootPath
            ?? Path.GetDirectoryName(_options.LocalServerProjectPath!)
            ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(_options.LocalServerProjectPath!);
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.Environment["ASPNETCORE_URLS"] = _options.UpstreamServerBaseAddress;

        return startInfo;
    }

    private ProcessStartInfo CreatePublishedServerStartInfo(string publishedServerExecutablePath)
    {
        var workingDirectory = Path.GetDirectoryName(publishedServerExecutablePath)
            ?? Environment.CurrentDirectory;

        var startInfo = new ProcessStartInfo
        {
            FileName = publishedServerExecutablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

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

            if (_ownedProcess is { HasExited: true } exitedProcess)
            {
                var failureMessage = CreateStartupFailureMessage(
                    $"BrowserCommanderServer exited with code {exitedProcess.ExitCode}.");
                CleanupExitedOwnedProcess();
                return EndpointAvailability.Unavailable(failureMessage);
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

    private async Task StopOwnedProcessAsync()
    {
        if (_ownedProcess is null)
        {
            return;
        }

        try
        {
            if (!_ownedProcess.HasExited)
            {
                _ownedProcess.Kill(entireProcessTree: true);
                using var timeoutSource = new CancellationTokenSource(ShutdownTimeout);
                await _ownedProcess.WaitForExitAsync(
                    timeoutSource.Token);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _ownedProcess.Dispose();
            _ownedProcess = null;
        }
    }
}
