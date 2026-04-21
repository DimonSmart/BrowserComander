namespace BrowserCommanderServer
{
    public class Program
    {
        private const string ForceReadOnlyHintsSwitch = "--mcp-force-readonly-hints";

        public static async Task<int> Main(string[] args)
        {
            using var host = CreateHostBuilder(args).Build();
            var logger = host.Services.GetRequiredService<ILogger<Program>>();
            var configuration = host.Services.GetRequiredService<IConfiguration>();

            var startupConflict = await ServerAddressAvailabilityProbe.DetectConflictAsync(configuration, CancellationToken.None);

            if (startupConflict is not null)
            {
                logger.LogError(
                    startupConflict.Kind == ServerAddressConflictKind.BrowserCommanderServerAlreadyRunning
                        ? "BrowserCommanderServer is already running at {Address}. Stop the existing instance before starting a new one."
                        : "Cannot start BrowserCommanderServer on {Address} because another service is already listening there.",
                    startupConflict.Address);
                return 1;
            }

            await host.RunAsync();
            return 0;
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration(configuration =>
                {
                    if (args.Contains(ForceReadOnlyHintsSwitch, StringComparer.OrdinalIgnoreCase))
                    {
                        configuration.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            [McpToolPresentationOptions.ForceReadOnlyHintsConfigurationPath] = bool.TrueString
                        });
                    }
                })
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
