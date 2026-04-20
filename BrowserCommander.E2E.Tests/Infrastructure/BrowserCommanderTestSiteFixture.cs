using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class BrowserCommanderTestSiteFixture : IAsyncDisposable
{
    private static readonly byte[] PngPixelBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4////fwAJ+wP9KobjigAAAABJRU5ErkJggg==");

    private WebApplication? _app;

    public Uri BaseUri { get; private set; } = null!;

    public async Task StartAsync()
    {
        if (_app is not null)
        {
            return;
        }

        var port = PortAllocator.GetLoopbackPort();
        BaseUri = new Uri($"http://127.0.0.1:{port}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = BrowserCommanderE2EEnvironment.TestSiteRootPath
        });

        builder.WebHost.UseKestrel();
        builder.WebHost.UseUrls(BaseUri.ToString());

        _app = builder.Build();

        var fileProvider = new PhysicalFileProvider(BrowserCommanderE2EEnvironment.TestSiteRootPath);
        _app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider
        });
        _app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider
        });

        _app.MapGet("/api/network/fetch", () => Results.Json(new
        {
            source = "fetch",
            ok = true,
            atUtc = DateTimeOffset.UtcNow
        }));

        _app.MapGet("/api/network/xhr", () => Results.Text("xhr-ok", "text/plain"));

        _app.MapGet("/api/network/image.png", () => Results.File(PngPixelBytes, "image/png"));

        _app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));

        await _app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is null)
        {
            return;
        }

        try
        {
            await _app.StopAsync();
        }
        finally
        {
            await _app.DisposeAsync();
        }
    }
}
