using Microsoft.Playwright;
using Xunit;

namespace BrowserCommander.E2E.Tests.Infrastructure;

public sealed class BrowserCommanderE2EFixture : IAsyncLifetime
{
    internal BrowserCommanderServerFixture Server { get; } = new();

    internal BrowserCommanderTestSiteFixture TestSite { get; } = new();

    public IPlaywright Playwright { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        BrowserCommanderE2EEnvironment.EnsureExtensionIsBuilt();

        Directory.CreateDirectory(BrowserCommanderE2EEnvironment.ArtifactsRootPath);

        await Server.StartAsync();
        await TestSite.StartAsync();
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        Playwright?.Dispose();
        await TestSite.DisposeAsync();
        await Server.DisposeAsync();
    }

    public Task<BrowserCommanderE2ESession> CreateSessionAsync(string testName)
    {
        return BrowserCommanderE2ESession.CreateAsync(
            Playwright,
            Server,
            TestSite,
            new TestArtifacts(testName));
    }
}
