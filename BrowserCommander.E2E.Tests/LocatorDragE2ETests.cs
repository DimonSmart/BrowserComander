using BrowserCommander.E2E.Tests.Infrastructure;
using BrowserCommander.Contracts;
using Microsoft.Playwright;
using Xunit;

namespace BrowserCommander.E2E.Tests;

public sealed class LocatorDragE2ETests : BrowserCommanderE2ETestBase
{
    public LocatorDragE2ETests(BrowserCommanderE2EFixture fixture)
        : base(fixture)
    {
    }

    [HeadedOnlyE2EFact]
    public Task LocatorDragTo_ExecutesRealMouseInteractionsAndValidation()
    {
        return ExecuteAsync(nameof(LocatorDragTo_ExecutesRealMouseInteractionsAndValidation), async session =>
        {
            var page = await session.OpenPageAsync("/index.html");
            var authorize = await session.Extension.AuthorizeTabAsync(page);
            Assert.True(authorize.Ok, authorize.Error);

            var pageRef = await session.WaitForSingleAuthorizedPageAsync();
            var pageId = pageRef.PageId;

            await AssertDragSuccessAsync(session, page, pageId, "left", "#drop-left", 1);
            await AssertDragSuccessAsync(session, page, pageId, "middle", "#drop-middle", 12);
            await AssertDragSuccessAsync(session, page, pageId, "right", "#drop-right", 6);

            var missingSource = await session.Mcp.CallBrowserResultAsync(
                "locator_drag_to",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["sourceSelector"] = "#missing-source",
                    ["targetSelector"] = "#drop-left",
                    ["button"] = "left",
                    ["moveSteps"] = 4,
                    ["timeoutMs"] = 1500
                });
            Assert.False(missingSource.Success);
            Assert.Equal(BrowserCommandErrorCodes.ElementNotFound, missingSource.ErrorCode);

            var missingTarget = await session.Mcp.CallBrowserResultAsync(
                "locator_drag_to",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["sourceSelector"] = "#drag-source",
                    ["targetSelector"] = "#missing-target",
                    ["button"] = "left",
                    ["moveSteps"] = 4,
                    ["timeoutMs"] = 1500
                });
            Assert.False(missingTarget.Success);
            Assert.Equal(BrowserCommandErrorCodes.ElementNotFound, missingTarget.ErrorCode);

            var hiddenSource = await session.Mcp.CallBrowserResultAsync(
                "locator_drag_to",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["sourceSelector"] = "#hidden-source",
                    ["targetSelector"] = "#drop-left",
                    ["button"] = "left",
                    ["moveSteps"] = 4,
                    ["timeoutMs"] = 1500
                });
            Assert.False(hiddenSource.Success);
            Assert.Equal(BrowserCommandErrorCodes.ElementNotVisible, hiddenSource.ErrorCode);

            var hiddenTarget = await session.Mcp.CallBrowserResultAsync(
                "locator_drag_to",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["sourceSelector"] = "#drag-source",
                    ["targetSelector"] = "#hidden-drop-target",
                    ["button"] = "left",
                    ["moveSteps"] = 4,
                    ["timeoutMs"] = 1500
                });
            Assert.False(hiddenTarget.Success);
            Assert.Equal(BrowserCommandErrorCodes.ElementNotVisible, hiddenTarget.ErrorCode);

            var invalidButton = await session.Mcp.CallBrowserResultAsync(
                "locator_drag_to",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["sourceSelector"] = "#drag-source",
                    ["targetSelector"] = "#drop-left",
                    ["button"] = "primary",
                    ["moveSteps"] = 4
                });
            Assert.False(invalidButton.Success);
            Assert.Equal(BrowserCommandErrorCodes.ValidationFailed, invalidButton.ErrorCode);

            var invalidMoveSteps = await session.Mcp.CallBrowserResultAsync(
                "locator_drag_to",
                new Dictionary<string, object?>
                {
                    ["pageId"] = pageId,
                    ["sourceSelector"] = "#drag-source",
                    ["targetSelector"] = "#drop-left",
                    ["button"] = "left",
                    ["moveSteps"] = 0
                });
            Assert.False(invalidMoveSteps.Success);
            Assert.Equal(BrowserCommandErrorCodes.ValidationFailed, invalidMoveSteps.ErrorCode);
        });
    }

    private static async Task AssertDragSuccessAsync(
        BrowserCommanderE2ESession session,
        IPage page,
        string pageId,
        string button,
        string targetSelector,
        int moveSteps)
    {
        var previousCount = await page.EvaluateAsync<int>(
            "selector => Number(document.querySelector(selector)?.dataset.dropCount ?? '0')",
            targetSelector);

        var result = await session.Mcp.CallBrowserResultAsync(
            "locator_drag_to",
            new Dictionary<string, object?>
            {
                ["pageId"] = pageId,
                ["sourceSelector"] = "#drag-source",
                ["targetSelector"] = targetSelector,
                ["button"] = button,
                ["moveSteps"] = moveSteps
            });
        Assert.True(result.Success, result.Error);

        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var dropState = await page.EvaluateAsync<DropState>(
                """
                selector => {
                  const target = document.querySelector(selector);
                  const status = document.querySelector('#drag-status');
                  return {
                    count: Number(target?.dataset.dropCount ?? '0'),
                    lastDrop: target?.dataset.lastDrop === 'true',
                    lastResult: status?.dataset.lastResult ?? '',
                    lastButton: status?.dataset.lastButton ?? '',
                    lastTarget: status?.dataset.lastTarget ?? '',
                    moveCount: Number(status?.dataset.moveCount ?? '0')
                  };
                }
                """,
                targetSelector);

            if (dropState.Count == previousCount + 1
                && dropState.LastDrop
                && dropState.LastResult == "success"
                && dropState.LastButton == button
                && dropState.LastTarget == targetSelector.TrimStart('#')
                && dropState.MoveCount > 0)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"Timed out waiting for drag success on '{targetSelector}'.");
    }

    private sealed class DropState
    {
        public int Count { get; set; }

        public bool LastDrop { get; set; }

        public string LastResult { get; set; } = string.Empty;

        public string LastButton { get; set; } = string.Empty;

        public string LastTarget { get; set; } = string.Empty;

        public int MoveCount { get; set; }
    }
}
