using BrowserCommander.E2E.Tests.Infrastructure;
using Microsoft.Playwright;
using Xunit;

namespace BrowserCommander.E2E.Tests;

/// <summary>
/// These tests deliberately exercise rendered Blazor pages, not only extension runtime messages.
/// The release workflow also runs them against the extracted release extension ZIP.
/// </summary>
public sealed class BlazorExtensionUiE2ETests : BrowserCommanderE2ETestBase
{
    public BlazorExtensionUiE2ETests(BrowserCommanderE2EFixture fixture) : base(fixture) { }

    [E2EFact]
    public Task Popup_RendersBlazor_WithoutRuntimeOrResourceFailures() =>
        ExecuteAsync(nameof(Popup_RendersBlazor_WithoutRuntimeOrResourceFailures), async session =>
        {
            var (popup, errors) = await OpenUiPageAsync(session, "popup.html", ".popup-container h1");
            Assert.Equal("BrowserCommander", await popup.Locator(".popup-container h1").InnerTextAsync());
            Assert.True(await popup.GetByText("Current tab").IsVisibleAsync());
            Assert.True(await popup.Locator(".popup-access-row").IsVisibleAsync());
            await AssertHealthyAsync(popup, errors);
        });

    [E2EFact]
    public Task Options_SaveFromBlazorUi_PersistsAfterReload() =>
        ExecuteAsync(nameof(Options_SaveFromBlazorUi_PersistsAfterReload), async session =>
        {
            var (options, errors) = await OpenUiPageAsync(session, "options.html", "#commandTimeout");
            Assert.Equal("Extension settings", await options.Locator("h1").InnerTextAsync());
            await options.WaitForFunctionAsync(
                "() => Number(document.querySelector('#commandTimeout')?.value) > 0 && " +
                "document.querySelector('#serverAddress')?.value?.startsWith('http')");
            var serverInput = options.Locator("#serverAddress");
            var initialAddress = await serverInput.InputValueAsync();
            var timeoutInput = options.Locator("#commandTimeout");
            var initialTimeout = int.Parse(await timeoutInput.InputValueAsync());
            var newTimeout = initialTimeout == 12345 ? 12346 : 12345;

            await timeoutInput.FillAsync(newTimeout.ToString());
            await options.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await options.Locator(".status-success").WaitForAsync();
            Assert.Equal(initialAddress, await serverInput.InputValueAsync());

            await options.ReloadAsync();
            await options.Locator("#commandTimeout").WaitForAsync();
            await options.WaitForFunctionAsync(
                "expected => document.querySelector('#commandTimeout')?.value === String(expected)", newTimeout);
            Assert.Equal(newTimeout.ToString(), await options.Locator("#commandTimeout").InputValueAsync());
            Assert.Equal(initialAddress, await options.Locator("#serverAddress").InputValueAsync());
            await AssertHealthyAsync(options, errors);
        });

    [E2EFact]
    public Task Popup_AuthorizeAndRevoke_UsesTheActualActiveHttpTab() =>
        ExecuteAsync(nameof(Popup_AuthorizeAndRevoke_UsesTheActualActiveHttpTab), async session =>
        {
            var target = await session.OpenPageAsync("/index.html");
            var expectedTabId = await session.Extension.GetTabIdAsync(target);
            var (popup, errors) = await OpenUiPageAsync(session, "popup.html", ".popup-container h1");

            // Opening popup.html as a tab activates the extension tab, unlike the real toolbar popup.
            // Restore the target as the active tab in the same window, then reload Blazor.
            // Verify the actual tab ID shown in the popup before authorizing anything.
            await session.Extension.ActivateTabAsync(target);
            await popup.ReloadAsync();
            await popup.Locator(".popup-container h1").WaitForAsync();
            await popup.WaitForFunctionAsync(
                "id => document.querySelector('.popup-current .popup-tab-id')?.textContent?.trim() === '#' + id",
                expectedTabId);
            Assert.Contains(target.Url, await popup.Locator(".popup-tab-url").InnerTextAsync());
            Assert.Contains("Not authorized", await popup.Locator(".popup-access-row").InnerTextAsync());

            await popup.GetByRole(AriaRole.Button, new() { Name = "Authorize tab" }).ClickAsync();
            await popup.WaitForFunctionAsync(
                "() => document.querySelector('.popup-access-row')?.textContent?.includes('Local: Authorized')");
            var authorized = await session.WaitForSingleAuthorizedPageAsync();
            Assert.Equal(expectedTabId, authorized.TabId);

            await popup.GetByRole(AriaRole.Button, new() { Name = "Revoke access" }).ClickAsync();
            await popup.WaitForFunctionAsync(
                "() => document.querySelector('.popup-access-row')?.textContent?.includes('Local: Not authorized')");
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline && (await session.Mcp.ListPagesAsync()).Count != 0)
            {
                await Task.Delay(200);
            }
            Assert.Empty(await session.Mcp.ListPagesAsync());
            await AssertHealthyAsync(popup, errors);
        });

    private static async Task<(IPage Page, List<string> Errors)> OpenUiPageAsync(
        BrowserCommanderE2ESession session, string fileName, string renderedSelector)
    {
        var errors = new List<string>();
        var page = await session.Context.NewPageAsync();
        page.PageError += (_, exception) => errors.Add("pageerror: " + exception);
        page.Response += (_, response) =>
        {
            if (response.Status >= 400 && response.Url.StartsWith("chrome-extension://", StringComparison.Ordinal))
            {
                errors.Add($"extension response {response.Status}: {response.Url}");
            }
        };

        await page.GotoAsync($"chrome-extension://{session.Extension.ExtensionId}/{fileName}");
        await page.Locator(renderedSelector).WaitForAsync(new() { Timeout = 30_000 });
        await AssertHealthyAsync(page, errors);
        return (page, errors);
    }

    private static async Task AssertHealthyAsync(IPage page, List<string> errors)
    {
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync(),
            "Blazor displayed an unhandled error.");
        Assert.False(await page.Locator("#app .loading-progress").IsVisibleAsync(),
            "Blazor is still displaying its loading progress.");
        Assert.True(await page.Locator("#app").IsVisibleAsync(),
            "Blazor did not render its application root.");
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }
}
