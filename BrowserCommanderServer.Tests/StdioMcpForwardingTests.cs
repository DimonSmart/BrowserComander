using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Xunit;

namespace BrowserCommanderServer.Tests;

/// <summary>
/// End-to-end stdio -> HTTP MCP forwarding without a browser extension.
/// Uses the real local HTTP tool server to exercise SDK serialization on both legs.
/// </summary>
public sealed class StdioMcpForwardingTests
{
    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2026-07-28")]
    public async Task EmptyPagesAndConnectionStatus_MatchDownstreamSchemas(string protocol)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var baseUrl = $"http://127.0.0.1:{port}";
        using var httpHost = BrowserCommanderServer.Program
            .CreateHostBuilder(["--urls", baseUrl]).Build();
        await httpHost.StartAsync();

        var binary = FindBinary("BrowserCommander.McpStdioBridge");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(binary);
        start.ArgumentList.Add(baseUrl + "/mcp");
        using var bridge = new Process { StartInfo = start };
        Assert.True(bridge.Start());

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            var nextId = 1;
            if (protocol == "2025-11-25")
            {
                using var init = await RpcAsync(bridge, """
                    {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"forwarding-test","version":"1"}}}
                    """, timeout.Token);
                Assert.True(init.RootElement.TryGetProperty("result", out _));
                await SendAsync(bridge, """
                    {"jsonrpc":"2.0","method":"notifications/initialized"}
                    """);
            }
            else
            {
                using var discovery = await RpcAsync(bridge, Request(nextId, "server/discover", new { }, protocol), timeout.Token);
                Assert.True(discovery.RootElement.TryGetProperty("result", out _));
            }

            using var toolsDoc = await RpcAsync(bridge, Request(++nextId, "tools/list", new { }, protocol), timeout.Token);
            var tools = toolsDoc.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray().ToArray();

            foreach (var name in new[] { "browser_list_pages", "browser_connection_status" })
            {
                var tool = Assert.Single(tools, t => t.GetProperty("name").GetString() == name);
                using var called = await RpcAsync(bridge,
                    Request(++nextId, "tools/call", new { name, arguments = new { } }, protocol), timeout.Token);
                var result = called.RootElement.GetProperty("result");
                Assert.False(result.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True);
                Assert.True(result.TryGetProperty("structuredContent", out var data));

                var schema = tool.GetProperty("outputSchema");
                if (protocol == "2025-11-25" && schema.TryGetProperty("properties", out var properties) &&
                    properties.TryGetProperty("result", out _))
                {
                    Assert.Equal(JsonValueKind.Object, data.ValueKind);
                    data = data.GetProperty("result");
                }

                Assert.Equal(name == "browser_list_pages" ? JsonValueKind.Array : JsonValueKind.Object,
                    data.ValueKind);
                if (name == "browser_list_pages")
                    Assert.Empty(data.EnumerateArray());
            }
        }
        finally
        {
            if (!bridge.HasExited)
                bridge.Kill(entireProcessTree: true);
            await bridge.WaitForExitAsync();
            await httpHost.StopAsync();
        }
    }

    private static string Request(int id, string method, object arguments, string protocol)
    {
        if (protocol == "2025-11-25")
            return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = arguments });

        var args = JsonSerializer.SerializeToElement(arguments);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, object>>(args.GetRawText())!;
        parameters["_meta"] = new Dictionary<string, object>
        {
            ["io.modelcontextprotocol/protocolVersion"] = protocol,
            ["io.modelcontextprotocol/clientInfo"] = new { name = "forwarding-test", version = "1" },
            ["io.modelcontextprotocol/clientCapabilities"] = new { }
        };
        return JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters });
    }

    private static async Task<JsonDocument> RpcAsync(Process bridge, string json, CancellationToken cancellationToken)
    {
        await SendAsync(bridge, json);
        var line = await bridge.StandardOutput.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(line));
        var message = JsonDocument.Parse(line);
        Assert.True(message.RootElement.TryGetProperty("result", out _), line);
        return message;
    }

    private static async Task SendAsync(Process bridge, string json)
    {
        await bridge.StandardInput.WriteLineAsync(json);
        await bridge.StandardInput.FlushAsync();
    }

    private static string FindBinary(string project)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var binary = Path.Combine(directory.FullName, project, "bin",
                configuration, "net8.0", project + ".dll");
            if (File.Exists(binary))
                return binary;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(project);
    }
}
