using Blazor.BrowserExtension;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Threading.Tasks;

namespace BrowserCommander
{
    public partial class BackgroundWorker : BackgroundWorkerBase
    {
        private BrowserCommanderConfig _serverConfig;

        [Inject]
        private BrowserCommanderConfig ServerConfig { get; set; }


        public override void OnInitialized()
        {
           var serverUrl = ServerConfig.ServerAddress;
           Logger.LogInformation($"Server url:{serverUrl}");
        }


        [BackgroundWorkerMain]
        public override void Main()
        {
            WebExtensions.Runtime.OnInstalled.AddListener(OnInstalled);
        }

        async Task OnInstalled()
        {
            var indexPageUrl = await WebExtensions.Runtime.GetURL("index.html");
            await WebExtensions.Tabs.Create(new()
            {
                Url = indexPageUrl
            });
        }
    }
}
