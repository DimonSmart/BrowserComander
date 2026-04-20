using System.Text.Json;
using BrowserCommander.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace BrowserCommander.E2E.Tests;

public sealed class LocatorToolsE2ETests : BrowserCommanderE2ETestBase
{
    public LocatorToolsE2ETests(BrowserCommanderE2EFixture fixture)
        : base(fixture)
    {
    }

    [E2EFact]
    public Task LocatorAndReadTools_ReflectRealDomState()
    {
        return ExecuteAsync(nameof(LocatorAndReadTools_ReflectRealDomState), async session =>
        {
            var page = await session.OpenPageAsync("/index.html");
            var authorize = await session.Extension.AuthorizeTabAsync(page);
            Assert.True(authorize.Ok, authorize.Error);

            var pageRef = await session.WaitForSingleAuthorizedPageAsync();
            var pageId = pageRef.PageId;

            var urlResult = await session.Mcp.CallBrowserResultAsync(
                "page_url",
                new Dictionary<string, object?> { ["pageId"] = pageId });
            Assert.True(urlResult.Success);
            Assert.Equal(page.Url, urlResult.Url);

            var titleResult = await session.Mcp.CallBrowserResultAsync(
                "page_title",
                new Dictionary<string, object?> { ["pageId"] = pageId });
            Assert.True(titleResult.Success);
            Assert.Equal("BrowserCommander E2E Index", titleResult.Title);

            var contentResult = await session.Mcp.CallBrowserResultAsync(
                "page_content",
                new Dictionary<string, object?> { ["pageId"] = pageId });
            Assert.True(contentResult.Success);
            Assert.Contains("BrowserCommander E2E Index", contentResult.Html);

            var evaluateResult = await session.Mcp.EvaluateAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["expression"] = "({ href: location.href, width: window.innerWidth, height: window.innerHeight })"
                });
            Assert.True(evaluateResult.Success);

            using (var document = JsonDocument.Parse(evaluateResult.ValueJson!))
            {
                Assert.Equal(page.Url, document.RootElement.GetProperty("href").GetString());
                Assert.True(document.RootElement.GetProperty("width").GetInt32() > 0);
                Assert.True(document.RootElement.GetProperty("height").GetInt32() > 0);
            }

            var byText = await session.Mcp.FindLocatorsAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["query"] = "Button 1",
                    ["onlyVisible"] = true,
                    ["interactiveOnly"] = true,
                    ["limit"] = 10
                });
            Assert.True(byText.Success);
            Assert.Contains(byText.Candidates, candidate => candidate.Selector == "#click-target");

            var byPlaceholder = await session.Mcp.FindLocatorsAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["query"] = "Fill input placeholder",
                    ["onlyVisible"] = true,
                    ["interactiveOnly"] = true,
                    ["limit"] = 10
                });
            Assert.Contains(byPlaceholder.Candidates, candidate => candidate.Selector == "#fill-input");

            var byAriaLabel = await session.Mcp.FindLocatorsAsync(
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["query"] = "Search field",
                    ["onlyVisible"] = true,
                    ["interactiveOnly"] = true,
                    ["limit"] = 10
                });
            Assert.Contains(byAriaLabel.Candidates, candidate => candidate.Selector == "#fill-input");

            var existsResult = await session.Mcp.CallBrowserResultAsync(
                "locator_exists",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#click-target"
                });
            Assert.True(existsResult.Success);
            Assert.True(existsResult.Exists);

            var countResult = await session.Mcp.CallBrowserResultAsync(
                "locator_count",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = ".counted-item"
                });
            Assert.True(countResult.Success);
            Assert.Equal(3, countResult.Count);

            var visibleResult = await session.Mcp.CallBrowserResultAsync(
                "locator_is_visible",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#click-target"
                });
            Assert.True(visibleResult.Success);
            Assert.True(visibleResult.Visible);

            await page.EvaluateAsync("window.browserCommanderE2E.scheduleShowHidden(150)");
            var waitVisibleResult = await session.Mcp.CallBrowserResultAsync(
                "locator_wait_for",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#hidden-target-state",
                    ["state"] = "visible"
                });
            Assert.True(waitVisibleResult.Success);
            await WaitForStateAsync(page, "#hidden-target-state", ElementExpectation.Visible);

            await page.EvaluateAsync("window.browserCommanderE2E.scheduleHideVisible(150)");
            var waitHiddenResult = await session.Mcp.CallBrowserResultAsync(
                "locator_wait_for",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#visible-target-state",
                    ["state"] = "hidden"
                });
            Assert.True(waitHiddenResult.Success);
            await WaitForStateAsync(page, "#visible-target-state", ElementExpectation.Hidden);

            await page.EvaluateAsync("window.browserCommanderE2E.scheduleDetachAttached(150)");
            var waitDetachedResult = await session.Mcp.CallBrowserResultAsync(
                "locator_wait_for",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#attached-target",
                    ["state"] = "detached"
                });
            Assert.True(waitDetachedResult.Success);
            await WaitForStateAsync(page, "#attached-target", ElementExpectation.Detached);

            await page.EvaluateAsync("window.browserCommanderE2E.scheduleAttachDetached(150)");
            var waitAttachedResult = await session.Mcp.CallBrowserResultAsync(
                "locator_wait_for",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#detached-target",
                    ["state"] = "attached"
                });
            Assert.True(waitAttachedResult.Success);
            await WaitForStateAsync(page, "#detached-target", ElementExpectation.Attached);

            var innerTextResult = await session.Mcp.CallBrowserResultAsync(
                "locator_inner_text",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#rich-text"
                });
            Assert.True(innerTextResult.Success);
            Assert.Equal("Rich text value", NormalizeWhitespace(innerTextResult.Text));

            var textContentResult = await session.Mcp.CallBrowserResultAsync(
                "locator_text_content",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#rich-text"
                });
            Assert.True(textContentResult.Success);
            Assert.Equal("Rich text value", NormalizeWhitespace(textContentResult.Text));

            var innerHtmlResult = await session.Mcp.CallBrowserResultAsync(
                "locator_inner_html",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#rich-text"
                });
            Assert.True(innerHtmlResult.Success);
            Assert.Contains("<strong>Rich</strong>", innerHtmlResult.Html);

            var fillInputResult = await session.Mcp.CallBrowserResultAsync(
                "locator_fill",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#fill-input",
                    ["value"] = "Filled input value"
                });
            Assert.True(fillInputResult.Success);
            Assert.Equal("Filled input value", await page.InputValueAsync("#fill-input"));

            var fillTextareaResult = await session.Mcp.CallBrowserResultAsync(
                "locator_fill",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#fill-textarea",
                    ["value"] = "Filled textarea value"
                });
            Assert.True(fillTextareaResult.Success);
            Assert.Equal("Filled textarea value", await page.InputValueAsync("#fill-textarea"));

            var inputValueResult = await session.Mcp.CallBrowserResultAsync(
                "locator_input_value",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#fill-input"
                });
            Assert.True(inputValueResult.Success);
            Assert.Equal("Filled input value", inputValueResult.Text);

            var clickResult = await session.Mcp.CallBrowserResultAsync(
                "locator_click",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#click-target"
                });
            Assert.True(clickResult.Success);
            await WaitForTextAsync(page, "#click-status", "Click count: 1");

            var pressResult = await session.Mcp.CallBrowserResultAsync(
                "locator_press",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["selector"] = "#press-target",
                    ["key"] = "Enter"
                });
            Assert.True(pressResult.Success);
            await WaitForTextAsync(page, "#press-status", "Last key: Enter");
        });
    }

    private static string NormalizeWhitespace(string? value)
    {
        return string.Join(
            " ",
            (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task WaitForTextAsync(IPage page, string selector, string expectedText)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var actualText = await page.TextContentAsync(selector) ?? string.Empty;
            if (actualText.Trim() == expectedText)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for '{selector}' to equal '{expectedText}'.");
    }

    private static async Task WaitForStateAsync(IPage page, string selector, ElementExpectation expectation)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var state = await page.EvaluateAsync<ElementState>(
                """
                selector => {
                  const element = document.querySelector(selector);
                  return {
                    exists: Boolean(element),
                    hidden: element ? element.classList.contains('is-hidden') || getComputedStyle(element).display === 'none' : false
                  };
                }
                """,
                selector);

            var isSatisfied = expectation switch
            {
                ElementExpectation.Visible => state.Exists && !state.Hidden,
                ElementExpectation.Hidden => state.Exists && state.Hidden,
                ElementExpectation.Detached => !state.Exists,
                ElementExpectation.Attached => state.Exists,
                _ => false
            };

            if (isSatisfied)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for selector '{selector}' to reach state '{expectation}'.");
    }

    private enum ElementExpectation
    {
        Visible,
        Hidden,
        Detached,
        Attached
    }

    private sealed class ElementState
    {
        public bool Exists { get; set; }

        public bool Hidden { get; set; }
    }
}
