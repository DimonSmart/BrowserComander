using System;
using System.Net.Http;
using System.Threading.Tasks;
using Blazor.BrowserExtension;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BrowserCommander;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebAssemblyHostBuilder.CreateDefault(args);
        builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

        var serverAddress = builder.Configuration.GetSection("ServerSettings:ServerAddress").Value ?? string.Empty;

        builder.Services.AddSingleton(new BrowserCommanderConfig
        {
            ServerAddress = serverAddress
        });

        builder.UseBrowserExtension(browserExtension =>
        {
            if (browserExtension.Mode != BrowserExtensionMode.Background)
            {
                builder.RootComponents.Add<App>("#app");
                builder.RootComponents.Add<HeadOutlet>("head::after");
            }
        });

        builder.Services.AddWebExtensions();
        builder.Services.AddScoped<JSInteropService>();
        builder.Services.AddScoped(_ => new HttpClient
        {
            BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
        });

        await builder.Build().RunAsync();
    }
}
