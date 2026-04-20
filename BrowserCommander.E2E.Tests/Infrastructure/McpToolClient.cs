using System.Text.Json;
using BrowserCommander.Contracts;
using BrowserCommanderServer;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class McpToolClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClientTransport _transport;
    private readonly McpClient _client;

    private McpToolClient(HttpClientTransport transport, McpClient client)
    {
        _transport = transport;
        _client = client;
    }

    public static async Task<McpToolClient> CreateAsync(Uri endpoint, CancellationToken cancellationToken = default)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "browsercommander-e2e",
            Endpoint = endpoint
        });

        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        return new McpToolClient(transport, client);
    }

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
        return tools.Select(tool => tool.ProtocolTool).ToArray();
    }

    public async Task<CallToolResult> CallRawAsync(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, object?>? serializedArguments = arguments?
            .Select(pair => new KeyValuePair<string, object?>(
                pair.Key,
                pair.Value ?? JsonSerializer.SerializeToElement(pair.Value, JsonOptions)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        return await _client.CallToolAsync(toolName, serializedArguments, cancellationToken: cancellationToken);
    }

    public async Task<T> CallStructuredAsync<T>(
        string toolName,
        IReadOnlyDictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var result = await CallRawAsync(toolName, arguments, cancellationToken);
        EnsureSuccess(toolName, result);

        if (TryDeserializeStructured<T>(result.StructuredContent, out var structuredValue))
        {
            return structuredValue;
        }

        var textPayload = string.Concat(result.Content?.OfType<TextContentBlock>().Select(block => block.Text) ?? []);
        if (TryDeserializeText<T>(textPayload, out var textValue))
        {
            return textValue;
        }

        var structuredJson = result.StructuredContent is null
            ? "<null>"
            : JsonSerializer.Serialize(result.StructuredContent, JsonOptions);

        throw new InvalidOperationException(
            $"Tool '{toolName}' result could not be deserialized to {typeof(T).Name}. StructuredContent={structuredJson}; TextContent={textPayload}");
    }

    public async Task<IReadOnlyList<BrowserPageSummary>> ListPagesAsync(CancellationToken cancellationToken = default) =>
        await CallStructuredAsync<List<BrowserPageSummary>>("browser_list_pages", cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<BrowserViewportPreset>> ListViewportPresetsAsync(CancellationToken cancellationToken = default) =>
        await CallStructuredAsync<List<BrowserViewportPreset>>("browser_list_viewport_presets", cancellationToken: cancellationToken);

    public Task<BrowserAutomationResult> CallBrowserResultAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        CallStructuredAsync<BrowserAutomationResult>(toolName, arguments, cancellationToken);

    public Task<BrowserLocatorSearchResult> FindLocatorsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        CallStructuredAsync<BrowserLocatorSearchResult>("page_find_locators", arguments, cancellationToken);

    public Task<BrowserEvaluateValue> EvaluateAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        CallStructuredAsync<BrowserEvaluateValue>("page_evaluate", arguments, cancellationToken);

    public Task<BrowserConsoleMessagesSnapshot> ReadConsoleMessagesAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        CallStructuredAsync<BrowserConsoleMessagesSnapshot>("page_console_messages", arguments, cancellationToken);

    public Task<BrowserNetworkRequestsSnapshot> ReadNetworkRequestsAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default) =>
        CallStructuredAsync<BrowserNetworkRequestsSnapshot>("page_network_requests", arguments, cancellationToken);

    public async Task<ImageContentBlock> CaptureScreenshotAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var result = await CallRawAsync("page_screenshot", arguments, cancellationToken);
        EnsureSuccess("page_screenshot", result);

        var image = result.Content?.OfType<ImageContentBlock>().FirstOrDefault();
        return image ?? throw new InvalidOperationException("page_screenshot did not return an image content block.");
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _transport.DisposeAsync();
    }

    private static void EnsureSuccess(string toolName, CallToolResult result)
    {
        if (result.IsError != true)
        {
            return;
        }

        var message = result.Content?.OfType<TextContentBlock>().Select(block => block.Text).FirstOrDefault()
                      ?? $"Tool '{toolName}' failed.";

        throw new InvalidOperationException(message);
    }

    private static bool TryDeserializeStructured<T>(object? structuredContent, out T value)
    {
        if (structuredContent is null)
        {
            value = default!;
            return false;
        }

        var json = JsonSerializer.Serialize(structuredContent, JsonOptions);
        return TryDeserializeText(json, out value);
    }

    private static bool TryDeserializeText<T>(string? json, out T value)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            value = default!;
            return false;
        }

        try
        {
            var deserialized = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (deserialized is null)
            {
                value = default!;
                return false;
            }

            value = deserialized;
            return true;
        }
        catch (JsonException)
        {
            value = default!;
            return false;
        }
    }
}
