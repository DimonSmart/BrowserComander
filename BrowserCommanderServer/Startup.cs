namespace BrowserCommanderServer
{
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            });

            services.AddSignalR(options =>
            {
                // page_content can return full HTML for large SPA pages, which easily exceeds
                // SignalR's default 32 KB incoming message limit and causes the hub connection
                // to be dropped before CompleteCommand is processed.
                options.MaximumReceiveMessageSize = 8 * 1024 * 1024;
            });

            services.AddCors(options =>
            {
                options.AddPolicy("AllowAll", policy =>
                {
                    policy
                        .AllowAnyMethod()
                        .AllowAnyHeader()
                        .SetIsOriginAllowed(_ => true)
                        .AllowCredentials();
                });
            });

            services.AddMcpServer()
                .WithHttpTransport()
                .WithToolsFromAssembly(typeof(BrowserAutomationMcpTools).Assembly);

            services.AddSingleton<IBrowserAutomationService, BrowserAutomationService>();
            services.AddHostedService<TimeBroadcastService>();
            services.AddHostedService<TunnelKeepaliveService>();
            services.AddControllers();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.Use(async (context, next) =>
            {
                context.Response.Headers[ServerIdentity.ResponseHeaderName] = ServerIdentity.ServiceName;
                await next();
            });

            app.UseCors("AllowAll");

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<BrowserCommanderHub>("/browserCommanderHub");
                endpoints.MapMcp("/mcp");
                endpoints.MapGet("/health", () => Results.Ok(new { status = "ok", service = ServerIdentity.ServiceName }));
                endpoints.MapGet("/whoami", () => Results.Ok(new { service = ServerIdentity.ServiceName }));
            });
        }
    }
}
