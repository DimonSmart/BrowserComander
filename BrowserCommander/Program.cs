using Blazor.BrowserExtension;
using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;

namespace BrowserCommander
{


    public static class Program
    {
        public static async Task Main(string[] args)
        {
              var builder = WebAssemblyHostBuilder.CreateDefault(args);

            builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);


            // Load configuration
            var serverAddress = builder.Configuration.GetSection("ServerSettings:ServerAddress").Value;

            builder.Services.AddSingleton(new BrowserCommanderConfig { ServerAddress = serverAddress });


            builder.Services.AddSingleton<HubConnection>(sp =>
            {
                var signalRurl = builder.HostEnvironment.BaseAddress + "browserCommanderHub";
                return new HubConnectionBuilder()
                    .WithUrl(signalRurl)
                    .WithAutomaticReconnect()
                    .Build();
            });

            builder.UseBrowserExtension(browserExtension =>
            {
                if (browserExtension.Mode == BrowserExtensionMode.Background)
                {
                    builder.RootComponents.AddBackgroundWorker<BackgroundWorker>();
                }
                else
                {
                    builder.RootComponents.Add<App>("#app");
                    builder.RootComponents.Add<HeadOutlet>("head::after");
                }
            });

            builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

            builder.Services.AddScoped<JSInteropService>();

            await builder.Build().RunAsync();
        }
    }
}
