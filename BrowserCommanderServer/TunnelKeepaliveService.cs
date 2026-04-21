using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace BrowserCommanderServer
{
    /// <summary>
    /// Periodically pings the configured tunnel URL so that the DevTunnel relay
    /// does not close the idle connection between ChatGPT requests.
    /// Configure via appsettings.json: "TunnelKeepalive": { "Url": "https://.../health" }
    /// Leave Url empty or unset to disable the service.
    /// </summary>
    public sealed class TunnelKeepaliveService : BackgroundService
    {
        private static readonly TimeSpan PingInterval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly ILogger<TunnelKeepaliveService> _logger;
        private readonly string? _url;
        private readonly HttpClient _http;

        public TunnelKeepaliveService(
            ILogger<TunnelKeepaliveService> logger,
            IConfiguration configuration)
        {
            _logger = logger;
            _url = configuration["TunnelKeepalive:Url"];
            _http = new HttpClient { Timeout = RequestTimeout };
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.IsNullOrWhiteSpace(_url))
            {
                _logger.LogInformation(
                    "TunnelKeepaliveService: no URL configured, keepalive disabled.");
                return;
            }

            _logger.LogInformation(
                "TunnelKeepaliveService: will ping {Url} every {Interval} min.",
                _url, PingInterval.TotalMinutes);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(PingInterval, stoppingToken);

                    try
                    {
                        using var response = await _http.GetAsync(_url, stoppingToken);
                        _logger.LogDebug(
                            "TunnelKeepaliveService: ping {Url} -> {Status}",
                            _url, (int)response.StatusCode);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            "TunnelKeepaliveService: ping to {Url} failed: {Error}",
                            _url,
                            ex.Message);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown is expected and should not be reported as a background-service failure.
            }
        }

        public override void Dispose()
        {
            _http.Dispose();
            base.Dispose();
        }
    }
}
