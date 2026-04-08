using System.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BrowserCommander.McpStdioBridge;

public sealed class UpstreamMcpProxy : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly StdioBridgeOptions _options;
    private readonly ServerProcessManager _serverProcessManager;
    private HttpClientTransport? _transport;
    private McpClient? _client;

    public UpstreamMcpProxy(
        StdioBridgeOptions options,
        ServerProcessManager serverProcessManager)
    {
        _options = options;
        _serverProcessManager = serverProcessManager;
    }

    public async Task<CallToolResult> CallToolAsync(
        CallToolRequestParams requestParams,
        CancellationToken cancellationToken)
    {
        var availability = await _serverProcessManager.EnsureEndpointAvailableAsync(cancellationToken);
        if (!availability.IsAvailable)
        {
            return CreateUnavailableResult(availability.FailureMessage);
        }

        try
        {
            return await CallUpstreamAsync(requestParams, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception initialFailure)
        {
            await ResetClientAsync();

            availability = await _serverProcessManager.EnsureEndpointAvailableAsync(cancellationToken);
            if (!availability.IsAvailable)
            {
                return CreateUnavailableResult(
                    $"{availability.FailureMessage} Original error: {initialFailure.Message}");
            }

            try
            {
                return await CallUpstreamAsync(requestParams, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception retryFailure)
            {
                await ResetClientAsync();

                if (!await _serverProcessManager.IsEndpointReachableAsync(cancellationToken))
                {
                    return CreateUnavailableResult(
                        $"Upstream MCP server '{_options.UpstreamEndpoint}' became unavailable. Last error: {retryFailure.Message}");
                }

                throw;
            }
        }
    }

    private async Task<McpClient> GetClientAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "browsercommander-stdio-bridge",
                Endpoint = _options.UpstreamEndpoint
            });

            try
            {
                _client = await McpClient.CreateAsync(transport);
                _transport = transport;
            }
            catch
            {
                if (transport is IAsyncDisposable asyncTransport)
                {
                    await asyncTransport.DisposeAsync();
                }

                throw;
            }

            return _client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private async Task<CallToolResult> CallUpstreamAsync(
        CallToolRequestParams requestParams,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        return await client.CallToolAsync(requestParams, cancellationToken);
    }

    private async Task ResetClientAsync()
    {
        await _connectGate.WaitAsync();
        try
        {
            if (_client is IAsyncDisposable client)
            {
                await client.DisposeAsync();
            }

            if (_transport is IAsyncDisposable transport)
            {
                await transport.DisposeAsync();
            }

            _client = null;
            _transport = null;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    private static CallToolResult CreateUnavailableResult(string? message)
    {
        return new CallToolResult
        {
            IsError = true,
            Content =
            [
                new TextContentBlock
                {
                    Text = string.IsNullOrWhiteSpace(message)
                        ? "Upstream MCP server is unavailable."
                        : message
                }
            ]
        };
    }

    public async ValueTask DisposeAsync()
    {
        await ResetClientAsync();
        _connectGate.Dispose();
    }
}
