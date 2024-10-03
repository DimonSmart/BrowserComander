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

            services.AddSignalR();

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

            services.AddSingleton<ITextStore, InMemoryTextStore>();
            services.AddHostedService<TimeBroadcastService>();
            services.AddControllers();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseCors("AllowAll");

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapControllers();
                endpoints.MapHub<BrowserCommanderHub>("/browserCommanderHub");
            });
        }
    }
}
