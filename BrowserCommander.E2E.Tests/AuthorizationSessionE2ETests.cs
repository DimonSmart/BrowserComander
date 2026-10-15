using BrowserCommander.E2E.Tests.Infrastructure;
using Xunit;

namespace BrowserCommander.E2E.Tests;

public sealed class AuthorizationSessionE2ETests : BrowserCommanderE2ETestBase
{
    public AuthorizationSessionE2ETests(BrowserCommanderE2EFixture fixture) : base(fixture) { }

    [E2EFact]
    public Task IndependentBrowserProfiles_HaveDistinctIds_AndRevocationIsLocal()
    {
        return ExecuteAsync(nameof(IndependentBrowserProfiles_HaveDistinctIds_AndRevocationIsLocal), async session =>
        {
            var other = await BrowserExtensionHarness.LaunchAsync(Fixture.Playwright, session.Server.BaseUri);
            try
            {
                var firstPage = await session.OpenPageAsync("/index.html");
                var secondPage = await other.Context.NewPageAsync();
                await secondPage.GotoAsync(new Uri(session.TestSite.BaseUri, "/nav-a.html").ToString());
                await secondPage.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.NetworkIdle);

                var firstGrant = await session.Extension.AuthorizeTabAsync(firstPage);
                var secondGrant = await other.AuthorizeTabAsync(secondPage);
                Assert.True(firstGrant.Ok, firstGrant.Error);
                Assert.True(secondGrant.Ok, secondGrant.Error);
                Assert.NotEqual(firstGrant.AgentId, secondGrant.AgentId);

                var available = await WaitForPagesAsync(session, 2);
                Assert.Equal(2, available.Select(p => p.AgentId).Distinct().Count());
                Assert.Equal(2, available.Select(p => p.PageId).Distinct().Count());

                var revoke = await session.Extension.RevokeTabAsync(firstPage);
                Assert.True(revoke.Ok, revoke.Error);
                var remaining = await WaitForPagesAsync(session, 1);
                Assert.Equal(secondGrant.AgentId, Assert.Single(remaining).AgentId);
                Assert.Single((await other.GetStatusAsync()).Connected
                    ? remaining : throw new InvalidOperationException("Other browser was disconnected."));
            }
            finally
            {
                await other.DisposeAsync();
            }
        });
    }

    private static async Task<IReadOnlyList<BrowserCommanderServer.BrowserPageSummary>> WaitForPagesAsync(
        BrowserCommanderE2ESession session, int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var pages = await session.Mcp.ListPagesAsync();
            if (pages.Count == count) return pages;
            await Task.Delay(150);
        }
        throw new TimeoutException($"Timed out waiting for {count} acknowledged browser pages.");
    }
}
