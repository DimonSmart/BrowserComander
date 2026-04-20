using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace BrowserCommanderServer
{
    public class TimeBroadcastService : BackgroundService
    {
        private readonly ILogger<TimeBroadcastService> _logger;
        private readonly IHubContext<BrowserCommanderHub> _hubContext;

        public TimeBroadcastService(ILogger<TimeBroadcastService> logger, IHubContext<BrowserCommanderHub> hubContext)
        {
            _logger = logger;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var currentTime = DateTime.Now.ToString("O");
                    _logger.LogInformation("Broadcasting current time: {CurrentTime}", currentTime);

                    await _hubContext.Clients.All.SendAsync("ReceiveTime", currentTime, stoppingToken);
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Host shutdown is expected and should not be reported as a background-service failure.
            }
        }
    }
}
