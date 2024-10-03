using Microsoft.AspNetCore.SignalR;

namespace BrowserCommanderServer
{
    // Startup class for configuring services and middleware
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
                        .SetIsOriginAllowed(origin => true)
                        .AllowCredentials();
                });
            });

            services.AddSingleton<ITextStore, InMemoryTextStore>();
        }

        public void Configure(IApplicationBuilder app)
        {
            app.UseCors("AllowAll");

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/getText", async context =>
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                    var textStore = context.RequestServices.GetRequiredService<ITextStore>();

                    var getLocator = context.Request.Query["getLocator"].ToString();

                    if (string.IsNullOrEmpty(getLocator))
                    {
                        logger.LogWarning("getText called without getLocator parameter.");
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new { message = "getLocator parameter is required." });
                        return;
                    }

                    logger.LogInformation("Received getText call with getLocator: {getLocator}", getLocator);

                    if (textStore.Texts.TryGetValue(getLocator, out var text))
                    {
                        await context.Response.WriteAsJsonAsync(new { text });
                    }
                    else
                    {
                        context.Response.StatusCode = StatusCodes.Status404NotFound;
                        await context.Response.WriteAsJsonAsync(new { message = "Text not found for the given getLocator." });
                    }
                });

                endpoints.MapPost("/setText", async context =>
                {
                    var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                    var textStore = context.RequestServices.GetRequiredService<ITextStore>();
                    var hubContext = context.RequestServices.GetService<IHubContext<BrowserCommanderHub>>();

                    try
                    {
                        var formData = await System.Text.Json.JsonSerializer.DeserializeAsync<SetTextRequest>(context.Request.Body);

                        if (formData == null || string.IsNullOrEmpty(formData.SetSelector) || string.IsNullOrEmpty(formData.Text))
                        {
                            logger.LogWarning("setText called with missing parameters.");
                            context.Response.StatusCode = StatusCodes.Status400BadRequest;
                            await context.Response.WriteAsJsonAsync(new { message = "setSelector and text parameters are required." });
                            return;
                        }

                        logger.LogInformation("Received setText call with setSelector: {setSelector}, text: {text}", formData.SetSelector, formData.Text);

                        textStore.Texts[formData.SetSelector] = formData.Text;

                        if (hubContext != null)
                        {
                            await hubContext.Clients.All.SendAsync("TextUpdated", formData.SetSelector, formData.Text);
                        }

                        await context.Response.WriteAsJsonAsync(new { message = "Text set successfully." });
                    }
                    catch (System.Exception ex)
                    {
                        logger.LogError(ex, "Error processing setText request.");
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new { message = "Invalid data." });
                    }
                });

                // Map the SignalR hub
                endpoints.MapHub<BrowserCommanderHub>("/browserCommanderHub");
            });
        }
    }
}
