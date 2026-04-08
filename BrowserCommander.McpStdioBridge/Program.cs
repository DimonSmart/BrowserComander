using BrowserCommander.McpStdioBridge;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

var builder = Host.CreateApplicationBuilder(args);
var localServerProjectPath = ResolveLocalServerProjectPath();
var publishedServerExecutablePath = ResolvePublishedServerExecutablePath();

builder.Logging.ClearProviders();

builder.Services.AddSingleton(new StdioBridgeOptions
{
    UpstreamEndpoint = ResolveUpstreamEndpoint(args),
    RepositoryRootPath = ResolveRepositoryRootPath(localServerProjectPath),
    LocalServerProjectPath = localServerProjectPath,
    PublishedServerExecutablePath = publishedServerExecutablePath
});

builder.Services.AddSingleton<BrowserCommanderToolCatalog>();
builder.Services.AddSingleton<ServerProcessManager>();
builder.Services.AddSingleton<UpstreamMcpProxy>();

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithListToolsHandler(HandleListToolsAsync)
    .WithCallToolHandler(HandleCallToolAsync);

var host = builder.Build();
var serverProcessManager = host.Services.GetRequiredService<ServerProcessManager>();
if (serverProcessManager.CanAutoStart)
{
    await serverProcessManager.EnsureEndpointAvailableAsync(CancellationToken.None);
}

await host.RunAsync();

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

static string? ResolveLocalServerProjectPath()
{
    foreach (var rootPath in EnumerateSearchRoots())
    {
        var currentPath = rootPath;
        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            var candidate = Path.Combine(
                currentPath,
                "BrowserCommanderServer",
                "BrowserCommanderServer.csproj");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            currentPath = Directory.GetParent(currentPath)?.FullName;
        }
    }

    return null;
}

static string? ResolveRepositoryRootPath(string? serverProjectPath)
{
    if (string.IsNullOrWhiteSpace(serverProjectPath))
    {
        return null;
    }

    var projectDirectoryPath = Path.GetDirectoryName(serverProjectPath);
    return string.IsNullOrWhiteSpace(projectDirectoryPath)
        ? null
        : Directory.GetParent(projectDirectoryPath)?.FullName;
}

static string? ResolvePublishedServerExecutablePath()
{
    foreach (var rootPath in EnumerateSearchRoots())
    {
        foreach (var fileName in new[]
        {
            "BrowserCommanderServer.exe",
            "BrowserCommanderServer"
        })
        {
            var candidate = Path.Combine(rootPath, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    return null;
}

static IEnumerable<string> EnumerateSearchRoots()
{
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in new[]
    {
        Environment.CurrentDirectory,
        AppContext.BaseDirectory
    })
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            continue;
        }

        var fullPath = Path.GetFullPath(candidate);
        if (seenPaths.Add(fullPath))
        {
            yield return fullPath;
        }
    }
}

static async ValueTask<ListToolsResult> HandleListToolsAsync(
    RequestContext<ListToolsRequestParams> requestContext,
    CancellationToken _)
{
    var services = requestContext.Services
        ?? throw new InvalidOperationException("Request services are unavailable.");
    var toolCatalog = services.GetRequiredService<BrowserCommanderToolCatalog>();

    return new ListToolsResult
    {
        Tools = toolCatalog.Tools.ToList()
    };
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
