using System.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BrowserCommander.McpStdioBridge;

public sealed class UpstreamMcpProxy : IAsyncDisposable
{
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly StdioBridgeOptions _options;
    private HttpClientTransport? _transport;
    private McpClient? _client;

    public UpstreamMcpProxy(StdioBridgeOptions options)
    {
        _options = options;
    }

    public async Task<ListToolsResult> ListToolsAsync(
        ListToolsRequestParams? requestParams,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        return await client.ListToolsAsync(requestParams ?? new ListToolsRequestParams(), cancellationToken);
    }

    public async Task<CallToolResult> CallToolAsync(
        CallToolRequestParams requestParams,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken);
        return await client.CallToolAsync(requestParams, cancellationToken);
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

            _transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = "browsercommander-stdio-bridge",
                Endpoint = _options.UpstreamEndpoint
            });

            _client = await McpClient.CreateAsync(_transport);
            return _client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable client)
        {
            await client.DisposeAsync();
        }

        if (_transport is IAsyncDisposable transport)
        {
            await transport.DisposeAsync();
        }

        _connectGate.Dispose();
    }
}
