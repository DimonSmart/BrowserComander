using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class StdioMcpWireTests
{
    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task RawStdioToolsList_UsesRequestedRevisionAndPublishesFullCatalog(string revision)
    {
        var binary = FindBridgeBinary();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(binary);
        // A non-/mcp path prevents local auto-start; tools/list must not
        // depend on a running browser or an available HTTP upstream.
        start.ArgumentList.Add("http://127.0.0.1:1/disabled-upstream");

        using var process = new Process { StartInfo = start };
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));

        try
        {
            if (revision == "2025-11-25")
            {
                using var hello = await SendAsync(process, """
                    {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"raw-wire-test","version":"1.0"}}}
                    """, timeout.Token);
                Assert.Equal("2025-11-25", hello.RootElement.GetProperty("result")
                    .GetProperty("protocolVersion").GetString());
                await WriteAsync(process, """
                    {"jsonrpc":"2.0","method":"notifications/initialized"}
                    """);
            }
            else
            {
                using var discovered = await SendAsync(process,
                    NewRequest(1, "server/discover"), timeout.Token);
                Assert.True(discovered.RootElement.TryGetProperty("result", out _));
            }

            using var listing = await SendAsync(process, revision == "2025-11-25"
                ? """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}"""
                : NewRequest(2, "tools/list"), timeout.Token);
            var tools = listing.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();
            Assert.Equal(31, tools.Length);
            var names = tools.Select(tool => tool.GetProperty("name").GetString()).ToArray();
            Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal), names);
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal("browser_connection_status", names[0]);
            Assert.Contains("browser_list_pages", names);
            Assert.Contains("page_screenshot", names);

            foreach (var tool in tools)
            {
                Assert.Equal("object", tool.GetProperty("inputSchema").GetProperty("type").GetString());
                if (!tool.TryGetProperty("outputSchema", out var schema))
                    continue;

                if (revision == "2025-11-25")
                {
                    Assert.Equal(JsonValueKind.Object, schema.ValueKind);
                    Assert.Equal("object", schema.GetProperty("type").GetString());
                }
            }

            // Same process, same negotiated revision: repeat on the wire.
            using var listingAgain = await SendAsync(process, revision == "2025-11-25"
                ? """{"jsonrpc":"2.0","id":3,"method":"tools/list","params":{}}"""
                : NewRequest(3, "tools/list"), timeout.Token);
            Assert.Equal(listing.RootElement.GetProperty("result").GetProperty("tools").GetRawText(),
                listingAgain.RootElement.GetProperty("result").GetProperty("tools").GetRawText());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static string NewRequest(int id, string method) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = new
            {
                _meta = new Dictionary<string, object>
                {
                    ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                    ["io.modelcontextprotocol/clientInfo"] = new { name = "raw-wire-test", version = "1.0" },
                    ["io.modelcontextprotocol/clientCapabilities"] = new { }
                }
            }
        });

    private static async Task<JsonDocument> SendAsync(Process process, string request, CancellationToken token)
    {
        await WriteAsync(process, request);
        var line = await process.StandardOutput.ReadLineAsync(token);
        Assert.False(string.IsNullOrEmpty(line));
        var document = JsonDocument.Parse(line);
        Assert.True(document.RootElement.TryGetProperty("result", out _),
            "MCP returned an error or non-JSON-RPC stdout: " + line);
        return document;
    }

    private static async Task WriteAsync(Process process, string request)
    {
        await process.StandardInput.WriteLineAsync(request);
        await process.StandardInput.FlushAsync();
    }

    private static string FindBridgeBinary()
    {
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "BrowserCommander.McpStdioBridge", "bin", config, "net8.0",
                "BrowserCommander.McpStdioBridge.dll");
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("The MCP stdio bridge project must be built before wire tests.");
    }
}
