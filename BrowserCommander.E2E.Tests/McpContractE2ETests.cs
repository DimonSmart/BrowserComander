using BrowserCommander.E2E.Tests.Infrastructure;
using Xunit;

namespace BrowserCommander.E2E.Tests;

public sealed class McpContractE2ETests : BrowserCommanderE2ETestBase
{
    public McpContractE2ETests(BrowserCommanderE2EFixture fixture)
        : base(fixture)
    {
    }

    [E2EFact]
    public Task ToolsAndAuthorizedPages_ArePublishedThroughMcp()
    {
        return ExecuteAsync(nameof(ToolsAndAuthorizedPages_ArePublishedThroughMcp), async session =>
        {
            var tools = await session.Mcp.ListToolsAsync();
            var toolNames = tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray();

            Assert.Equal(30, toolNames.Length);
            Assert.Equal(
                [
                    "browser_list_pages",
                    "browser_list_viewport_presets",
                    "locator_click",
                    "locator_count",
                    "locator_drag_to",
                    "locator_exists",
                    "locator_fill",
                    "locator_inner_html",
                    "locator_inner_text",
                    "locator_input_value",
                    "locator_is_visible",
                    "locator_press",
                    "locator_text_content",
                    "locator_wait_for",
                    "page_clear_viewport_override",
                    "page_console_messages",
                    "page_content",
                    "page_evaluate",
                    "page_find_locators",
                    "page_go_back",
                    "page_go_forward",
                    "page_goto",
                    "page_network_requests",
                    "page_reload",
                    "page_screenshot",
                    "page_set_viewport_preset",
                    "page_title",
                    "page_url",
                    "page_wait_for_load_state",
                    "page_wait_for_url"
                ],
                toolNames);

            Assert.Empty(await session.Mcp.ListPagesAsync());

            var presets = await session.Mcp.ListViewportPresetsAsync();
            Assert.Contains(presets, preset => preset.Name == "iphone-se");
            Assert.Contains(presets, preset => preset.Name == "iphone-12-pro");
            Assert.Contains(presets, preset => preset.Name == "pixel-7");
            Assert.Contains(presets, preset => preset.Name == "galaxy-s20-ultra");

            var page = await session.OpenPageAsync("/index.html");

            var authorize = await session.Extension.AuthorizeTabAsync(page);
            Assert.True(authorize.Ok, authorize.Error);

            var published = await session.WaitForSingleAuthorizedPageAsync();
            Assert.Equal(page.Url, published.Url);
            Assert.Equal("BrowserCommander E2E Index", published.Title);

            var revoke = await session.Extension.RevokeTabAsync(page);
            Assert.True(revoke.Ok, revoke.Error);

            await WaitForPageCountAsync(session, 0);
        });
    }

    private static async Task WaitForPageCountAsync(
        BrowserCommanderE2ESession session,
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var pages = await session.Mcp.ListPagesAsync(cancellationToken);
            if (pages.Count == expectedCount)
            {
                return;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {expectedCount} authorized page(s).");
    }
}
