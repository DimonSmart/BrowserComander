using System.IO;

namespace BrowserCommanderServer
{
    public class Startup
    {
        private readonly IConfiguration _configuration;

        public Startup(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            var toolPresentationOptions = _configuration
                .GetSection(McpToolPresentationOptions.SectionName)
                .Get<McpToolPresentationOptions>()
                ?? new McpToolPresentationOptions();

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

            var toolCatalog = new BrowserCommanderHttpToolCatalog(toolPresentationOptions);

            services.AddMcpServer()
                .WithHttpTransport()
                .WithTools(toolCatalog.Tools);

            services.AddSingleton<IBrowserAutomationService, BrowserAutomationService>();
            services.AddHostedService<TimeBroadcastService>();
            services.AddHostedService<TunnelKeepaliveService>();
            services.AddControllers();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment environment)
        {
            var faviconPath = Path.Combine(environment.ContentRootPath, "wwwroot", "favicon.ico");

            app.Use(async (context, next) =>
            {
                context.Response.Headers[ServerIdentity.ResponseHeaderName] = ServerIdentity.ServiceName;
                await next();
            });

            app.UseWhen(
                context => context.Request.Path.StartsWithSegments("/mcp"),
                branch => branch.UseMiddleware<McpTransportLoggingMiddleware>());

            app.UseCors("AllowAll");

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<BrowserCommanderHub>("/browserCommanderHub");
                endpoints.MapMcp("/mcp");
                endpoints.MapGet(
                    "/favicon.ico",
                    () => File.Exists(faviconPath)
                        ? Results.File(faviconPath, "image/x-icon")
                        : Results.NotFound());
                endpoints.MapGet("/health", () => Results.Ok(new { status = "ok", service = ServerIdentity.ServiceName }));
                endpoints.MapGet("/whoami", () => Results.Ok(new { service = ServerIdentity.ServiceName }));
            });
        }
    }
}
