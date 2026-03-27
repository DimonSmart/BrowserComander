using BrowserCommander.McpStdioBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();

builder.Services.AddSingleton(new StdioBridgeOptions
{
    UpstreamEndpoint = ResolveUpstreamEndpoint(args)
});

builder.Services.AddSingleton<UpstreamMcpProxy>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithListToolsHandler(HandleListToolsAsync)
    .WithCallToolHandler(HandleCallToolAsync);

await builder.Build().RunAsync();

static Uri ResolveUpstreamEndpoint(string[] args)
{
    var candidate = args.FirstOrDefault()
        ?? Environment.GetEnvironmentVariable("BROWSER_COMMANDER_MCP_HTTP_URL")
        ?? "http://localhost:5082/mcp";

    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
    {
        throw new InvalidOperationException(
            $"Invalid upstream MCP endpoint '{candidate}'.");
    }

    return uri;
}

static async ValueTask<ListToolsResult> HandleListToolsAsync(
    RequestContext<ListToolsRequestParams> requestContext,
    CancellationToken cancellationToken)
{
    var services = requestContext.Services
        ?? throw new InvalidOperationException("Request services are unavailable.");
    var proxy = services.GetRequiredService<UpstreamMcpProxy>();
    return await proxy.ListToolsAsync(requestContext.Params, cancellationToken);
}

static async ValueTask<CallToolResult> HandleCallToolAsync(
    RequestContext<CallToolRequestParams> requestContext,
    CancellationToken cancellationToken)
{
    var services = requestContext.Services
        ?? throw new InvalidOperationException("Request services are unavailable.");
    var requestParams = requestContext.Params
        ?? throw new InvalidOperationException("CallTool params are unavailable.");
    var proxy = services.GetRequiredService<UpstreamMcpProxy>();
    return await proxy.CallToolAsync(requestParams, cancellationToken);
}
