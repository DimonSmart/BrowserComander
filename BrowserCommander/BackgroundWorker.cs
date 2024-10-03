using Blazor.BrowserExtension;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using System;

namespace BrowserCommander
{
    public partial class BackgroundWorker : BackgroundWorkerBase
    {
        [Inject]
        private BrowserCommanderConfig ServerConfig { get; set; }

        [Inject]
        private HubConnection HubConnection { get; set; }

        [Inject]
        private ILogger<BackgroundWorker> Logger { get; set; }

        public override void OnInitialized()
        {
            var serverUrl = ServerConfig.ServerAddress;
            Logger.LogInformation($"Server url: {serverUrl}");
            Logger.LogInformation($"HubConnection: {(HubConnection != null)}");
        }

        [BackgroundWorkerMain]
        public override void Main()
        {
            WebExtensions.Runtime.OnInstalled.AddListener(OnInstalled);
        }

        private async Task StartHubConnectionAsync()
        {
            try
            {
                HubConnection.On<string>("ReceiveCommand", async (command) =>
                {
                    Logger.LogInformation($"Received command from server: {command}");
                    await HandleCommandAsync(command);
                });

                await HubConnection.StartAsync();
                Logger.LogInformation("SignalR connection started.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error starting SignalR connection: {ex.Message}");
            }
        }

        private async Task HandleCommandAsync(string command)
        {
            // TODO: Implement logic to handle the command
        }

        async Task OnInstalled()
        {
            var indexPageUrl = await WebExtensions.Runtime.GetURL("index.html");
            await WebExtensions.Tabs.Create(new()
            {
                Url = indexPageUrl
            });

            await StartHubConnectionAsync();
        }
    }
}
