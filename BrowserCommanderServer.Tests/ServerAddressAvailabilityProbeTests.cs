using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class ServerAddressAvailabilityProbeTests
{
    [Fact]
    public void GetConfiguredAddresses_SplitsConfiguredUrls()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [WebHostDefaults.ServerUrlsKey] = "http://localhost:5082;https://localhost:7093"
            })
            .Build();

        var addresses = ServerAddressAvailabilityProbe.GetConfiguredAddresses(configuration);

        Assert.Equal(
            ["http://localhost:5082", "https://localhost:7093"],
            addresses);
    }

    [Fact]
    public async Task DetectConflictAsync_ReturnsSameService_WhenBrowserCommanderServerIsAlreadyRunning()
    {
        await using var server = await FakeHttpServer.StartAsync(
            statusCode: "200 OK",
            headers: [($"{ServerIdentity.ResponseHeaderName}", ServerIdentity.ServiceName)],
            body: $$"""{"service":"{{ServerIdentity.ServiceName}}"}""",
            expectedRequests: 1);

        var configuration = CreateConfiguration(server.Address);

        var conflict = await ServerAddressAvailabilityProbe.DetectConflictAsync(configuration, CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(server.Address, conflict.Address);
        Assert.Equal(ServerAddressConflictKind.BrowserCommanderServerAlreadyRunning, conflict.Kind);
    }

    [Fact]
    public async Task DetectConflictAsync_ReturnsOtherService_WhenAddressBelongsToAnotherHttpService()
    {
        await using var server = await FakeHttpServer.StartAsync(
            statusCode: "200 OK",
            headers: [],
            body: """{"service":"SomeOtherService"}""",
            expectedRequests: 2);

        var configuration = CreateConfiguration(server.Address);

        var conflict = await ServerAddressAvailabilityProbe.DetectConflictAsync(configuration, CancellationToken.None);

        Assert.NotNull(conflict);
        Assert.Equal(server.Address, conflict.Address);
        Assert.Equal(ServerAddressConflictKind.OtherServiceListening, conflict.Kind);
    }

    [Fact]
    public async Task DetectConflictAsync_ReturnsNull_WhenConfiguredAddressIsFree()
    {
        var freePort = GetFreePort();
        var configuration = CreateConfiguration($"http://127.0.0.1:{freePort}");

        var conflict = await ServerAddressAvailabilityProbe.DetectConflictAsync(configuration, CancellationToken.None);

        Assert.Null(conflict);
    }

    private static IConfiguration CreateConfiguration(string address) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Urls"] = address
            })
            .Build();

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class FakeHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _serveTask;

        private FakeHttpServer(TcpListener listener, Task serveTask)
        {
            _listener = listener;
            _serveTask = serveTask;
            Address = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        }

        public string Address { get; }

        public static Task<FakeHttpServer> StartAsync(
            string statusCode,
            IReadOnlyList<(string Name, string Value)> headers,
            string body,
            int expectedRequests)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var responseBytes = BuildResponse(statusCode, headers, body);
            var serveTask = Task.Run(async () =>
            {
                for (var index = 0; index < expectedRequests; index++)
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    using var stream = client.GetStream();
                    await ReadRequestAsync(stream);
                    await stream.WriteAsync(responseBytes);
                }
            });

            return Task.FromResult(new FakeHttpServer(listener, serveTask));
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();

            try
            {
                await _serveTask;
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private static byte[] BuildResponse(
            string statusCode,
            IReadOnlyList<(string Name, string Value)> headers,
            string body)
        {
            var builder = new StringBuilder()
                .Append("HTTP/1.1 ")
                .Append(statusCode)
                .Append("\r\n")
                .Append("Content-Type: application/json\r\n")
                .Append("Connection: close\r\n")
                .Append("Content-Length: ")
                .Append(Encoding.UTF8.GetByteCount(body))
                .Append("\r\n");

            foreach (var (name, value) in headers)
            {
                builder.Append(name).Append(": ").Append(value).Append("\r\n");
            }

            builder.Append("\r\n").Append(body);
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        private static async Task ReadRequestAsync(NetworkStream stream)
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);

            while (true)
            {
                var line = await reader.ReadLineAsync();

                if (string.IsNullOrEmpty(line))
                {
                    break;
                }
            }
        }
    }
}
