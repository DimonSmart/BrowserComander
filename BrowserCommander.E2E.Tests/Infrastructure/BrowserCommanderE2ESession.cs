using Microsoft.Playwright;

namespace BrowserCommander.E2E.Tests.Infrastructure;

public sealed class BrowserCommanderE2ESession : IAsyncDisposable
{
    private bool _artifactsCaptured;

    private BrowserCommanderE2ESession(
        BrowserCommanderServerFixture server,
        BrowserCommanderTestSiteFixture testSite,
        BrowserExtensionHarness extension,
        McpToolClient mcp,
        TestArtifacts artifacts)
    {
        Server = server;
        TestSite = testSite;
        Extension = extension;
        Mcp = mcp;
        Artifacts = artifacts;
    }

    internal BrowserCommanderServerFixture Server { get; }

    internal BrowserCommanderTestSiteFixture TestSite { get; }

    internal BrowserExtensionHarness Extension { get; }

    internal McpToolClient Mcp { get; }

    internal TestArtifacts Artifacts { get; }

    internal IBrowserContext Context => Extension.Context;

    internal static async Task<BrowserCommanderE2ESession> CreateAsync(
        IPlaywright playwright,
        BrowserCommanderServerFixture server,
        BrowserCommanderTestSiteFixture testSite,
        TestArtifacts artifacts)
    {
        var extension = await BrowserExtensionHarness.LaunchAsync(playwright, server.BaseUri);
        await extension.Context.Tracing.StartAsync(new TracingStartOptions
        {
            Screenshots = true,
            Snapshots = true,
            Sources = true
        });

        var mcp = await McpToolClient.CreateAsync(server.McpEndpoint);
        return new BrowserCommanderE2ESession(server, testSite, extension, mcp, artifacts);
    }

    public async Task<IPage> OpenPageAsync(string relativePath)
    {
        var page = await Context.NewPageAsync();
        await page.GotoAsync(new Uri(TestSite.BaseUri, relativePath).ToString());
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        return page;
    }

    public async Task<BrowserCommanderServer.BrowserPageSummary> WaitForSingleAuthorizedPageAsync(
        int timeoutMs = 30_000,
        CancellationToken cancellationToken = default)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pages = await Mcp.ListPagesAsync(cancellationToken);
            if (pages.Count == 1)
            {
                return pages[0];
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for a single authorized MCP page.");
    }

    public async Task CaptureFailureArtifactsAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        if (_artifactsCaptured)
        {
            return;
        }

        _artifactsCaptured = true;

        await Artifacts.WriteTextAsync("exception.txt", exception.ToString(), cancellationToken);
        await Artifacts.WriteTextAsync("server.log", Server.Logs.GetSnapshot(), cancellationToken);

        var contentPages = Context.Pages
            .Where(page => !page.Url.StartsWith("chrome-extension://", StringComparison.Ordinal))
            .ToArray();

        for (var index = 0; index < contentPages.Length; index++)
        {
            await Artifacts.CaptureScreenshotAsync(contentPages[index], $"page-{index + 1}.png", cancellationToken);
        }

        await Context.Tracing.StopAsync(new TracingStopOptions
        {
            Path = Artifacts.GetPath("trace.zip")
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (!_artifactsCaptured)
        {
            await Context.Tracing.StopAsync();
        }

        await Mcp.DisposeAsync();
        await Extension.DisposeAsync();
    }
}
