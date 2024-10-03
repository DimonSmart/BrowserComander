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
            // Run the loop until the service is stopped
            while (!stoppingToken.IsCancellationRequested)
            {
                var currentTime = DateTime.Now.ToString("O"); // ISO 8601 format
                _logger.LogInformation("Broadcasting current time: {CurrentTime}", currentTime);

                // Send the current time to all connected clients
                await _hubContext.Clients.All.SendAsync("ReceiveTime", currentTime);

                // Wait for one minute
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }
}
