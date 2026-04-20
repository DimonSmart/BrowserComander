using System.Text.Json;
using BrowserCommander.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace BrowserCommander.E2E.Tests;

public sealed class NavigationAndDebuggerE2ETests : BrowserCommanderE2ETestBase
{
    public NavigationAndDebuggerE2ETests(BrowserCommanderE2EFixture fixture)
        : base(fixture)
    {
    }

    [E2EFact]
    public Task NavigationConsoleNetworkScreenshotAndViewportTools_WorkInRealBrowser()
    {
        return ExecuteAsync(nameof(NavigationConsoleNetworkScreenshotAndViewportTools_WorkInRealBrowser), async session =>
        {
            var page = await session.OpenPageAsync("/index.html");
            var authorize = await session.Extension.AuthorizeTabAsync(page);
            Assert.True(authorize.Ok, authorize.Error);

            var pageRef = await session.WaitForSingleAuthorizedPageAsync();
            var pageId = pageRef.PageId;

            await session.Mcp.ReadConsoleMessagesAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["clearBuffer"] = true
                });

            await page.ClickAsync("#emit-console-button");
            await page.ClickAsync("#emit-error-button");
            await Task.Delay(400);

            var consoleMessages = await session.Mcp.ReadConsoleMessagesAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["limit"] = 20,
                    ["clearBuffer"] = true
                });
            Assert.True(consoleMessages.Success);
            Assert.Contains(consoleMessages.Entries, entry => entry.Text?.Contains("browsercommander-e2e-console", StringComparison.Ordinal) == true);
            Assert.Contains(consoleMessages.Entries, entry => entry.Text?.Contains("browsercommander-e2e-error", StringComparison.Ordinal) == true);

            var consoleAfterClear = await session.Mcp.ReadConsoleMessagesAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["limit"] = 20
                });
            Assert.Empty(consoleAfterClear.Entries);

            await session.Mcp.ReadNetworkRequestsAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["clearBuffer"] = true
                });

            await page.ClickAsync("#send-fetch-button");
            await page.ClickAsync("#send-xhr-button");
            await page.ClickAsync("#load-image-button");
            await WaitForImageLoadAsync(page, "#network-image");

            var networkRequests = await session.Mcp.ReadNetworkRequestsAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["limit"] = 30,
                    ["clearBuffer"] = true
                });
            Assert.True(networkRequests.Success);
            Assert.Contains(networkRequests.Entries, entry => entry.Url?.Contains("/api/network/fetch", StringComparison.Ordinal) == true);
            Assert.Contains(networkRequests.Entries, entry => entry.Url?.Contains("/api/network/xhr", StringComparison.Ordinal) == true);
            Assert.Contains(networkRequests.Entries, entry => entry.Url?.Contains("/api/network/image.png", StringComparison.Ordinal) == true);

            var networkAfterClear = await session.Mcp.ReadNetworkRequestsAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["limit"] = 30
                });
            Assert.Empty(networkAfterClear.Entries);

            var initialViewport = await ReadViewportAsync(session, pageId);
            Assert.True(initialViewport.Width > 0);
            Assert.True(initialViewport.Height > 0);

            var setPresetResult = await session.Mcp.CallBrowserResultAsync(
                "page_set_viewport_preset",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["preset"] = "iphone-12-pro",
                    ["orientation"] = "portrait"
                });
            Assert.True(setPresetResult.Success);
            await WaitForViewportAsync(session, pageId, 390, 844);

            var clearViewportResult = await session.Mcp.CallBrowserResultAsync(
                "page_clear_viewport_override",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId
                });
            Assert.True(clearViewportResult.Success);
            await WaitForViewportDifferenceAsync(session, pageId, 390, 844);

            var gotoResult = await session.Mcp.CallBrowserResultAsync(
                "page_goto",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["url"] = new Uri(session.TestSite.BaseUri, "/nav-a.html").ToString(),
                    ["waitUntil"] = "domcontentloaded"
                });
            Assert.True(gotoResult.Success);
            await WaitForUrlAsync(page, "nav-a.html");

            var waitUrlResult = await session.Mcp.CallBrowserResultAsync(
                "page_wait_for_url",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["url"] = "nav-a.html",
                    ["matchMode"] = "contains"
                });
            Assert.True(waitUrlResult.Success);
            Assert.Contains("nav-a.html", waitUrlResult.Url);

            var gotoSecondResult = await session.Mcp.CallBrowserResultAsync(
                "page_goto",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["url"] = new Uri(session.TestSite.BaseUri, "/nav-b.html").ToString(),
                    ["waitUntil"] = "load"
                });
            Assert.True(gotoSecondResult.Success);
            await WaitForUrlAsync(page, "nav-b.html");

            var backResult = await session.Mcp.CallBrowserResultAsync(
                "page_go_back",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["waitUntil"] = "load"
                });
            Assert.True(backResult.Success);
            await WaitForUrlAsync(page, "nav-a.html");

            var forwardResult = await session.Mcp.CallBrowserResultAsync(
                "page_go_forward",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["waitUntil"] = "load"
                });
            Assert.True(forwardResult.Success);
            await WaitForUrlAsync(page, "nav-b.html");

            var reloadResult = await session.Mcp.CallBrowserResultAsync(
                "page_reload",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["waitUntil"] = "load"
                });
            Assert.True(reloadResult.Success);

            var loadStateResult = await session.Mcp.CallBrowserResultAsync(
                "page_wait_for_load_state",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["state"] = "load"
                });
            Assert.True(loadStateResult.Success);
            Assert.Equal("complete", loadStateResult.ReadyState);
        });
    }

    private static async Task<ViewportSize> ReadViewportAsync(BrowserCommanderE2ESession session, string pageId)
    {
        var result = await session.Mcp.EvaluateAsync(
            new Dictionary<string, object?>
            {
                ["pageId"] = pageId,
                ["expression"] = "({ width: window.innerWidth, height: window.innerHeight })"
            });

        using var document = JsonDocument.Parse(result.ValueJson!);
        return new ViewportSize
        {
            Width = document.RootElement.GetProperty("width").GetInt32(),
            Height = document.RootElement.GetProperty("height").GetInt32()
        };
    }

    private static async Task WaitForViewportAsync(BrowserCommanderE2ESession session, string pageId, int width, int height)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var viewport = await ReadViewportAsync(session, pageId);
            if (viewport.Width == width && viewport.Height == height)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"Timed out waiting for viewport {width}x{height}.");
    }

    private static async Task WaitForViewportDifferenceAsync(BrowserCommanderE2ESession session, string pageId, int previousWidth, int previousHeight)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var viewport = await ReadViewportAsync(session, pageId);
            if (viewport.Width != previousWidth || viewport.Height != previousHeight)
            {
                return;
            }

            await Task.Delay(200);
        }

        throw new TimeoutException("Timed out waiting for viewport override to clear.");
    }

    private static async Task WaitForUrlAsync(IPage page, string urlSuffix)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            if (page.Url.Contains(urlSuffix, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for url containing '{urlSuffix}'.");
    }

    private static async Task WaitForImageLoadAsync(IPage page, string selector)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var loaded = await page.EvaluateAsync<bool>(
                """
                selector => {
                  const image = document.querySelector(selector);
                  return image instanceof HTMLImageElement && image.complete && image.naturalWidth > 0;
                }
                """,
                selector);

            if (loaded)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for image '{selector}' to load.");
    }

    private sealed class ViewportSize
    {
        public int Width { get; set; }

        public int Height { get; set; }
    }
}
