using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class BrowserCommanderServerFixture : IAsyncDisposable
{
    private IHost? _host;

    public Uri BaseUri { get; private set; } = null!;

    public Uri McpEndpoint => new(BaseUri, "/mcp");

    public InMemoryLoggerProvider Logs { get; } = new();

    public async Task StartAsync()
    {
        if (_host is not null)
        {
            return;
        }

        var port = PortAllocator.GetLoopbackPort();
        BaseUri = new Uri($"http://127.0.0.1:{port}");

        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseKestrel();
                webBuilder.UseUrls(BaseUri.ToString());
                webBuilder.UseSetting(WebHostDefaults.ServerUrlsKey, BaseUri.ToString());
                webBuilder.ConfigureAppConfiguration(configurationBuilder =>
                {
                    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Urls"] = BaseUri.ToString(),
                        ["TunnelKeepalive:Url"] = string.Empty
                    });
                });
                webBuilder.UseStartup<BrowserCommanderServer.Startup>();
            })
            .Build();

        _host.Services.GetRequiredService<ILoggerFactory>().AddProvider(Logs);

        await _host.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is null)
        {
            Logs.Dispose();
            return;
        }

        try
        {
            await _host.StopAsync();
        }
        finally
        {
            _host.Dispose();
            Logs.Dispose();
        }
    }
}
