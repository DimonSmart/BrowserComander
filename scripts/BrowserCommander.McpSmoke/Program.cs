using System.Text.Json;
using ModelContextProtocol.Client;

var transport = CreateTransport(args);

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync();
Console.WriteLine("TOOLS=" + string.Join(
    ",",
    tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal)));

var pagesResult = await client.CallToolAsync(
    "browser_list_pages",
    new Dictionary<string, object?>());

Console.WriteLine("PAGES_RESULT=" + JsonSerializer.Serialize(pagesResult));

static IClientTransport CreateTransport(string[] args)
{
    if (args.Length > 0 && string.Equals(args[0], "stdio", StringComparison.OrdinalIgnoreCase))
    {
        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "browsercommander-mcp-smoke-stdio",
            Command = "dotnet",
            WorkingDirectory = GetRepositoryRoot(),
            Arguments =
            [
                "run",
                "--project",
                GetBridgeProjectPath(),
                "--no-build",
                "--",
                args.Length > 1 ? args[1] : "http://localhost:5082/mcp"
            ]
        });
    }

    var endpoint = args.Length > 0
        ? args[0]
        : "http://localhost:5082/mcp";

    return new HttpClientTransport(new HttpClientTransportOptions
    {
        Name = "browsercommander-mcp-smoke-http",
        Endpoint = new Uri(endpoint)
    });
}

static string GetRepositoryRoot()
{
    return Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..",
        "..",
        "..",
        "..",
        ".."));
}

static string GetBridgeProjectPath()
{
    return Path.Combine(
        GetRepositoryRoot(),
        "BrowserCommander.McpStdioBridge",
        "BrowserCommander.McpStdioBridge.csproj");
}
