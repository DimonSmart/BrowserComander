using Blazor.BrowserExtension;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using System;
using WebExtensions.Net.Runtime;
using System.Linq;

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

        [Inject]
        private JSInteropService JSInteropService { get; set; }



        public override void OnInitialized()
        {
            var serverUrl = ServerConfig.ServerAddress;
            Logger.LogInformation($"Server url: {serverUrl}");
            Logger.LogInformation($"HubConnection: {(HubConnection != null)}");
            Logger.LogInformation($"JSInteropService: {(JSInteropService != null)}");
        }

        [BackgroundWorkerMain]
        public override void Main()
        {
            WebExtensions.Runtime.OnInstalled.AddListener(OnInstalled);
            WebExtensions.Runtime.OnMessage.AddListener(OnContentScriptMessageReceived);
        }

        private bool OnContentScriptMessageReceived(object arg1, MessageSender sender, Action<object> action)
        {
            Logger.LogInformation($"OnContentScriptMessageReceived: {arg1}");
            return true;
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

                HubConnection.On<string>("ReceiveTime", async (currentTime) =>
                {
                    Logger.LogInformation($"Current server time: {currentTime}");

                    try
                    {
                        var selector = "div#prompt-textarea[contenteditable=\"true\"]";
                        var result = await JSInteropService.GetTextAsync(selector);
                        Logger.LogInformation($"Field:{result}");

                    }
                    catch (Exception exception)
                    {
                        Logger.LogInformation($"Exception: {exception}");
                    }
                });

                await HubConnection.StartAsync();
                Logger.LogInformation("SignalR connection started.");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error starting SignalR connection: {ex.Message}");
            }
        }

        private async Task SendMessageToContentScriptAsync(object message)
        {
            Logger.LogError($"SendMessageToContentScriptAsync: {message}");
            var tabs = (await WebExtensions.Tabs.Query(new WebExtensions.Net.Tabs.QueryInfo() { Active = true, CurrentWindow = true })).ToList();
            if (tabs.Count > 0)
            {
                await WebExtensions.Tabs.SendMessage(tabs[0].Id.Value, message);
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
