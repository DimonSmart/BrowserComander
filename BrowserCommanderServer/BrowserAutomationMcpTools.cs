using System.ComponentModel;
using System.Text.Json;
using BrowserCommander.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BrowserCommanderServer;

[McpServerToolType]
public static class BrowserAutomationMcpTools
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [McpServerTool(
        Name = "browser_list_pages",
        Title = "List Authorized Pages",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Lists browser pages that the browser user explicitly authorized for automation. Each page includes a stable pageId used by the other MCP tools.")]
    public static IReadOnlyCollection<BrowserPageSummary> ListPages(IServiceProvider services)
    {
        return GetAutomationService(services).GetPages();
    }

    [McpServerTool(
        Name = "browser_list_viewport_presets",
        Title = "List Viewport Presets",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Lists the built-in phone viewport presets that can be applied to authorized pages. These presets control viewport size only, not full mobile emulation.")]
    public static IReadOnlyCollection<BrowserViewportPreset> ListViewportPresets()
    {
        return BrowserViewportPresetCatalog.All.ToArray();
    }

    [McpServerTool(
        Name = "page_url",
        Title = "Get Page URL",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the current URL of an already-open authorized page.")]
    public static Task<BrowserAutomationResult> PageUrl(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageUrl(),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_title",
        Title = "Get Page Title",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the current title of an already-open authorized page.")]
    public static Task<BrowserAutomationResult> PageTitle(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageTitle(),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_content",
        Title = "Get Page Content",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the raw full HTML of the page, similar to Playwright page.content(). Use this only as a last resort when targeted tools were insufficient. Prefer browser_list_pages, page_find_locators, locator_* reads, and other focused queries first because full-page HTML is often large, noisy, and expensive for LLM context.")]
    public static Task<BrowserAutomationResult> PageContent(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageContent(),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_find_locators",
        Title = "Find Locator Candidates",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Searches the page for likely locator candidates and returns suggested CSS selectors plus matching diagnostics. Use this to discover a locator without falling back to raw page_evaluate.")]
    public static async Task<BrowserLocatorSearchResult> PageFindLocators(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Free-text query to search for in placeholder, aria-label, text, title, id, role, or name. Can be empty to list top interactive candidates.")] string? query = null,
        [Description("Whether to only return visible candidates.")] bool onlyVisible = true,
        [Description("Whether to limit the search to interactive elements.")] bool interactiveOnly = true,
        [Description("Maximum number of candidates to return.")] int limit = 20,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecutePageCommandAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageFindLocators(
                query,
                onlyVisible,
                interactiveOnly,
                NormalizeLimit(limit)),
            timeoutMs,
            cancellationToken);

        return CreateLocatorSearchResult(result, pageId, query, onlyVisible, interactiveOnly);
    }

    [McpServerTool(
        Name = "page_evaluate",
        Title = "Evaluate JavaScript On Page",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Evaluates a JavaScript expression against the already-open authorized page using the browser debugger protocol.")]
    public static async Task<BrowserEvaluateValue> PageEvaluate(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("JavaScript expression to evaluate.")] string expression,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecutePageCommandAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageEvaluate(expression),
            timeoutMs,
            cancellationToken);

        return CreateEvaluateValue(result, pageId);
    }

    [McpServerTool(
        Name = "page_screenshot",
        Title = "Capture Page Screenshot",
        ReadOnly = true,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = false)]
    [Description("Captures a screenshot of the authorized page using the browser debugger protocol.")]
    public static async Task<CallToolResult> PageScreenshot(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Screenshot format. Supported values: png, jpeg, webp.")] string format = "png",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecutePageCommandAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageScreenshot(format),
            timeoutMs,
            cancellationToken);

        return CreateScreenshotToolResult(result, format);
    }

    [McpServerTool(
        Name = "page_console_messages",
        Title = "Read Page Console Messages",
        ReadOnly = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns recent console and runtime messages collected from the authorized page via the browser debugger protocol.")]
    public static async Task<BrowserConsoleMessagesSnapshot> PageConsoleMessages(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Maximum number of messages to return.")] int limit = 100,
        [Description("Whether to clear the in-memory buffer after returning the messages.")] bool clearBuffer = false,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecutePageCommandAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageConsoleMessages(
                NormalizeLimit(limit),
                clearBuffer),
            timeoutMs,
            cancellationToken);

        return CreateConsoleMessagesSnapshot(result, pageId);
    }

    [McpServerTool(
        Name = "page_network_requests",
        Title = "Read Page Network Requests",
        ReadOnly = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns recent network activity collected from the authorized page via the browser debugger protocol.")]
    public static async Task<BrowserNetworkRequestsSnapshot> PageNetworkRequests(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Maximum number of requests to return.")] int limit = 100,
        [Description("Whether to clear the in-memory buffer after returning the requests.")] bool clearBuffer = false,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        var result = await ExecutePageCommandAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageNetworkRequests(
                NormalizeLimit(limit),
                clearBuffer),
            timeoutMs,
            cancellationToken);

        return CreateNetworkRequestsSnapshot(result, pageId);
    }

    [McpServerTool(
        Name = "page_set_viewport_preset",
        Title = "Apply Viewport Preset",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Applies a built-in phone viewport preset to the authorized page. This changes viewport size only and does not perform full mobile emulation.")]
    public static Task<BrowserAutomationResult> PageSetViewportPreset(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Preset name returned by browser_list_viewport_presets.")] string preset,
        [Description("Viewport orientation. Supported values: portrait, landscape.")] string orientation = "portrait",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        if (!TryCreateViewportPresetPlan(
                preset,
                orientation,
                out var plan,
                out var failureResult))
        {
            return Task.FromResult(failureResult);
        }

        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandActions.PageSetViewportSize,
            plan,
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_clear_viewport_override",
        Title = "Clear Viewport Override",
        ReadOnly = false,
        Destructive = false,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Clears any active viewport-size override on the authorized page and returns it to the browser's normal desktop viewport.")]
    public static Task<BrowserAutomationResult> PageClearViewportOverride(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandActions.PageClearViewportOverride,
            BrowserCommandPlanBuilder.PageClearViewportOverride(),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_goto",
        Title = "Navigate Page",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Navigates an already-open authorized page to a new URL and waits for the requested load state.")]
    public static Task<BrowserAutomationResult> PageGoto(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Destination URL.")] string url,
        [Description("Load state to wait for. Supported values: load, domcontentloaded.")] string waitUntil = "load",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 30000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageGoto(url, waitUntil),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_reload",
        Title = "Reload Page",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Reloads an already-open authorized page and waits for the requested load state.")]
    public static Task<BrowserAutomationResult> PageReload(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Load state to wait for. Supported values: load, domcontentloaded.")] string waitUntil = "load",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 30000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageReload(waitUntil),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_go_back",
        Title = "Go Back",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Navigates the page back in history and waits for the requested load state.")]
    public static Task<BrowserAutomationResult> PageGoBack(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Load state to wait for. Supported values: load, domcontentloaded.")] string waitUntil = "load",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 30000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageGoBack(waitUntil),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_go_forward",
        Title = "Go Forward",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Navigates the page forward in history and waits for the requested load state.")]
    public static Task<BrowserAutomationResult> PageGoForward(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Load state to wait for. Supported values: load, domcontentloaded.")] string waitUntil = "load",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 30000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageGoForward(waitUntil),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_wait_for_url",
        Title = "Wait For URL",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Waits until the page URL matches the expected value. Supported matchMode values: exact, contains, glob, regex.")]
    public static Task<BrowserAutomationResult> PageWaitForUrl(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Expected URL pattern.")] string url,
        [Description("How to match the URL. Supported values: exact, contains, glob, regex.")] string matchMode = "glob",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 30000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageWaitForUrl(url, matchMode),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "page_wait_for_load_state",
        Title = "Wait For Load State",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Waits until the page reaches the requested load state. Supported values: load, domcontentloaded.")]
    public static Task<BrowserAutomationResult> PageWaitForLoadState(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("Load state to wait for. Supported values: load, domcontentloaded.")] string state = "load",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 30000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.PageWaitForLoadState(state),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_click",
        Title = "Click Locator",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Clicks a locator on an already-open authorized page. Uses CSS selectors.")]
    public static Task<BrowserAutomationResult> LocatorClick(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorClick(selector),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_drag_to",
        Title = "Drag Locator To Locator",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Drags from the center of one locator to the center of another locator using real mouse events. Supports left, middle, and right mouse buttons.")]
    public static Task<BrowserAutomationResult> LocatorDragTo(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the drag source element.")] string sourceSelector,
        [Description("CSS selector of the drag target element.")] string targetSelector,
        [Description("Mouse button to hold during the drag. Supported values: left, middle, right.")] string button = "left",
        [Description("Number of intermediate mouse move events between source and target. Supported range: 1 to 100.")] int moveSteps = 12,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        if (!TryValidateLocatorDragArguments(
                sourceSelector,
                targetSelector,
                button,
                moveSteps,
                out var normalizedButton,
                out var failureResult))
        {
            return Task.FromResult(failureResult);
        }

        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorDragTo(
                sourceSelector,
                targetSelector,
                normalizedButton,
                moveSteps),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_fill",
        Title = "Fill Locator",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Fills a text-entry locator on an already-open authorized page. Supports input, textarea, select, and contenteditable elements.")]
    public static Task<BrowserAutomationResult> LocatorFill(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Text to write into the locator.")] string value,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorFill(selector, value),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_press",
        Title = "Press Locator Key",
        ReadOnly = false,
        Destructive = false,
        Idempotent = false,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Focuses a locator on an already-open authorized page and presses a keyboard key, such as Enter.")]
    public static Task<BrowserAutomationResult> LocatorPress(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Keyboard key to press, for example Enter, Tab, Escape, ArrowDown, or a single character.")] string key,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorPress(selector, key),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_inner_text",
        Title = "Get Locator Inner Text",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the innerText of the first locator match.")]
    public static Task<BrowserAutomationResult> LocatorInnerText(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorInnerText(selector),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_text_content",
        Title = "Get Locator Text Content",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the textContent of the first locator match.")]
    public static Task<BrowserAutomationResult> LocatorTextContent(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorTextContent(selector),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_inner_html",
        Title = "Get Locator Inner HTML",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the innerHTML of the first locator match.")]
    public static Task<BrowserAutomationResult> LocatorInnerHtml(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorInnerHtml(selector),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_input_value",
        Title = "Get Locator Input Value",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Returns the current input value of the first locator match.")]
    public static Task<BrowserAutomationResult> LocatorInputValue(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorInputValue(selector),
            timeoutMs,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_exists",
        Title = "Check Locator Exists",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Checks whether a locator exists on the authorized page.")]
    public static Task<BrowserAutomationResult> LocatorExists(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorExists(selector),
            1000,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_count",
        Title = "Count Locator Matches",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Counts how many elements match a locator on the authorized page.")]
    public static Task<BrowserAutomationResult> LocatorCount(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target elements.")] string selector,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorCount(selector),
            1000,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_is_visible",
        Title = "Check Locator Visibility",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Checks whether the first locator match is visible on the authorized page.")]
    public static Task<BrowserAutomationResult> LocatorIsVisible(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorIsVisible(selector),
            1000,
            cancellationToken);
    }

    [McpServerTool(
        Name = "locator_wait_for",
        Title = "Wait For Locator",
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        UseStructuredContent = true)]
    [Description("Waits until the locator reaches the requested state. Supported states: attached, detached, visible, hidden.")]
    public static Task<BrowserAutomationResult> LocatorWaitFor(
        [Description("Page identifier returned by browser_list_pages.")] string pageId,
        [Description("CSS selector of the target element.")] string selector,
        [Description("Target wait state. Supported values: attached, detached, visible, hidden.")] string state = "visible",
        [Description("Optional timeout in milliseconds.")] int timeoutMs = 10000,
        IServiceProvider services = null!,
        CancellationToken cancellationToken = default)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandPlanBuilder.LocatorWaitFor(selector, state),
            timeoutMs,
            cancellationToken);
    }

    private static IBrowserAutomationService GetAutomationService(IServiceProvider services)
    {
        return services.GetRequiredService<IBrowserAutomationService>();
    }

    private static Task<BrowserAutomationResult> ExecutePageCommandAsync(
        IServiceProvider services,
        string pageId,
        BrowserExecutionPlan plan,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandActions.ExecutePlan,
            plan,
            timeoutMs,
            cancellationToken);
    }

    private static Task<BrowserAutomationResult> ExecutePlanAsync(
        IServiceProvider services,
        string pageId,
        string action,
        BrowserExecutionPlan plan,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        if (!TryResolvePageId(
                services,
                pageId,
                action,
                out var resolvedPageId,
                out var failureResult))
        {
            return Task.FromResult(failureResult);
        }

        if (!TryCreatePagePlanCommand(resolvedPageId, plan, timeoutMs, out var command, out failureResult))
        {
            return Task.FromResult(failureResult);
        }

        return ExecuteAsync(services, command, cancellationToken);
    }

    private static Task<BrowserAutomationResult> ExecutePlanAsync(
        IServiceProvider services,
        string pageId,
        BrowserExecutionPlan plan,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        return ExecutePlanAsync(
            services,
            pageId,
            BrowserCommandActions.ExecutePlan,
            plan,
            timeoutMs,
            cancellationToken);
    }

    private static Task<BrowserAutomationResult> ExecutePageCommandAsync(
        IServiceProvider services,
        string pageId,
        string action,
        int timeoutMs,
        CancellationToken cancellationToken,
        Action<BrowserAutomationCommand>? configure = null)
    {
        if (!TryResolvePageId(
                services,
                pageId,
                action,
                out var resolvedPageId,
                out var failureResult))
        {
            return Task.FromResult(failureResult);
        }

        if (!TryCreatePageCommand(resolvedPageId, action, timeoutMs, configure, out var command, out failureResult))
        {
            return Task.FromResult(failureResult);
        }

        return ExecuteAsync(services, command, cancellationToken);
    }

    private static bool TryResolvePageId(
        IServiceProvider services,
        string pageId,
        string action,
        out string resolvedPageId,
        out BrowserAutomationResult failureResult)
    {
        if (!string.IsNullOrWhiteSpace(pageId)
            && !string.Equals(pageId, "default", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(pageId, "current", StringComparison.OrdinalIgnoreCase))
        {
            resolvedPageId = pageId;
            failureResult = new BrowserAutomationResult();
            return true;
        }

        var pages = GetAutomationService(services).GetPages();
        if (pages.Count == 1)
        {
            resolvedPageId = pages.First().PageId;
            failureResult = new BrowserAutomationResult();
            return true;
        }

        var activePages = pages.Where(page => page.Active).ToList();
        if (activePages.Count == 1)
        {
            resolvedPageId = activePages[0].PageId;
            failureResult = new BrowserAutomationResult();
            return true;
        }

        resolvedPageId = string.Empty;
        failureResult = CreateValidationFailureResult(
            pageId,
            action,
            pages.Count == 0
                ? "No browser pages are connected."
                : "PageId is required because multiple pages are available.");
        return false;
    }

    private static bool TryCreatePagePlanCommand(
        string pageId,
        BrowserExecutionPlan plan,
        int timeoutMs,
        out BrowserAutomationCommand command,
        out BrowserAutomationResult failureResult)
    {
        if (!BrowserPageRef.TryParse(pageId, out var pageRef))
        {
            command = new BrowserAutomationCommand();
            failureResult = CreateValidationFailureResult(
                pageId,
                BrowserCommandActions.ExecutePlan,
                CreateInvalidPageIdMessage(pageId));
            return false;
        }

        command = new BrowserAutomationCommand
        {
            AgentId = pageRef.AgentId,
            TabId = pageRef.TabId,
            Action = BrowserCommandActions.ExecutePlan,
            TimeoutMs = NormalizeTimeout(timeoutMs),
            Plan = plan
        };

        failureResult = new BrowserAutomationResult();
        return true;
    }

    private static Task<BrowserAutomationResult> ExecuteLocatorCommandAsync(
        IServiceProvider services,
        string pageId,
        string action,
        string selector,
        int timeoutMs,
        CancellationToken cancellationToken,
        Action<BrowserAutomationCommand>? configure = null)
    {
        return ExecutePageCommandAsync(
            services,
            pageId,
            action,
            timeoutMs,
            cancellationToken,
            command =>
            {
                command.Selector = selector;
                configure?.Invoke(command);
            });
    }

    private static bool TryCreatePageCommand(
        string pageId,
        string action,
        int timeoutMs,
        Action<BrowserAutomationCommand>? configure,
        out BrowserAutomationCommand command,
        out BrowserAutomationResult failureResult)
    {
        if (!BrowserPageRef.TryParse(pageId, out var pageRef))
        {
            command = new BrowserAutomationCommand();
            failureResult = CreateValidationFailureResult(
                pageId,
                action,
                CreateInvalidPageIdMessage(pageId));
            return false;
        }

        command = new BrowserAutomationCommand
        {
            AgentId = pageRef.AgentId,
            TabId = pageRef.TabId,
            Action = action,
            TimeoutMs = NormalizeTimeout(timeoutMs)
        };

        configure?.Invoke(command);
        failureResult = new BrowserAutomationResult();
        return true;
    }

    private static Task<BrowserAutomationResult> ExecuteAsync(
        IServiceProvider services,
        BrowserAutomationCommand command,
        CancellationToken cancellationToken)
    {
        return GetAutomationService(services).ExecuteCommandAsync(command, cancellationToken);
    }

    private static int NormalizeTimeout(int timeoutMs)
    {
        return timeoutMs > 0 ? timeoutMs : 10000;
    }

    private static int NormalizeLimit(int limit)
    {
        return limit > 0 ? limit : 100;
    }

    private static bool TryValidateLocatorDragArguments(
        string? sourceSelector,
        string? targetSelector,
        string? button,
        int moveSteps,
        out string normalizedButton,
        out BrowserAutomationResult failureResult)
    {
        if (string.IsNullOrWhiteSpace(sourceSelector))
        {
            normalizedButton = string.Empty;
            failureResult = CreateValidationFailureResult(
                pageId: null,
                action: BrowserCommandActions.LocatorDragTo,
                error: "SourceSelector is required.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetSelector))
        {
            normalizedButton = string.Empty;
            failureResult = CreateValidationFailureResult(
                pageId: null,
                action: BrowserCommandActions.LocatorDragTo,
                error: "TargetSelector is required.");
            return false;
        }

        normalizedButton = NormalizeMouseButtonOrDefault(button);
        if (normalizedButton.Length == 0)
        {
            failureResult = CreateValidationFailureResult(
                pageId: null,
                action: BrowserCommandActions.LocatorDragTo,
                error: $"Unsupported button '{button}'. Supported values: left, middle, right.");
            return false;
        }

        if (moveSteps is < 1 or > 100)
        {
            failureResult = CreateValidationFailureResult(
                pageId: null,
                action: BrowserCommandActions.LocatorDragTo,
                error: "MoveSteps must be an integer between 1 and 100.");
            return false;
        }

        failureResult = new BrowserAutomationResult();
        return true;
    }

    private static string NormalizeMouseButtonOrDefault(string? button)
    {
        var normalizedButton = button?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedButton))
        {
            return "left";
        }

        return normalizedButton is "left" or "middle" or "right"
            ? normalizedButton
            : string.Empty;
    }

    private static bool TryCreateViewportPresetPlan(
        string? presetName,
        string? orientation,
        out BrowserExecutionPlan plan,
        out BrowserAutomationResult failureResult)
    {
        if (!BrowserViewportPresetCatalog.TryGetByName(presetName, out var preset))
        {
            plan = new BrowserExecutionPlan();
            failureResult = CreateValidationFailureResult(
                pageId: null,
                action: BrowserCommandActions.PageSetViewportSize,
                error: $"Unknown viewport preset '{presetName}'. Use browser_list_viewport_presets to discover supported names.");
            return false;
        }

        if (!TryResolveViewportDimensions(preset, orientation, out var width, out var height, out var error))
        {
            plan = new BrowserExecutionPlan();
            failureResult = CreateValidationFailureResult(
                pageId: null,
                action: BrowserCommandActions.PageSetViewportSize,
                error: error);
            return false;
        }

        plan = BrowserCommandPlanBuilder.PageSetViewportSize(width, height);
        failureResult = new BrowserAutomationResult();
        return true;
    }

    private static bool TryResolveViewportDimensions(
        BrowserViewportPreset preset,
        string? orientation,
        out int width,
        out int height,
        out string error)
    {
        var normalizedOrientation = orientation?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedOrientation)
            || string.Equals(normalizedOrientation, "portrait", StringComparison.OrdinalIgnoreCase))
        {
            width = preset.Width;
            height = preset.Height;
            error = string.Empty;
            return true;
        }

        if (string.Equals(normalizedOrientation, "landscape", StringComparison.OrdinalIgnoreCase))
        {
            width = preset.Height;
            height = preset.Width;
            error = string.Empty;
            return true;
        }

        width = 0;
        height = 0;
        error = $"Unsupported orientation '{orientation}'. Supported values: portrait, landscape.";
        return false;
    }

    private static string CreateInvalidPageIdMessage(string? pageId)
    {
        return $"Invalid pageId '{pageId}'. Expected the exact value returned by browser_list_pages, for example 'page:<browserSessionId>:<tabId>', or use 'current' when a single page is available.";
    }

    private static List<TItem> DeserializeList<TItem>(string? json)
    {
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<TItem>>(json, JsonOptions) ?? [];
    }

    private static BrowserAutomationResult CreateValidationFailureResult(
        string? pageId,
        string action,
        string error)
    {
        var parsed = BrowserPageRef.TryParse(pageId, out var pageRef)
            ? pageRef
            : default;

        return new BrowserAutomationResult
        {
            AgentId = parsed.AgentId ?? string.Empty,
            TabId = parsed.TabId,
            Action = action,
            Success = false,
            ErrorCode = BrowserCommandErrorCodes.ValidationFailed,
            Error = error
        };
    }

    private static BrowserLocatorSearchResult CreateLocatorSearchResult(
        BrowserAutomationResult result,
        string pageId,
        string? query,
        bool onlyVisible,
        bool interactiveOnly)
    {
        return new BrowserLocatorSearchResult
        {
            PageId = pageId,
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            Error = result.Error,
            Query = query ?? string.Empty,
            OnlyVisible = onlyVisible,
            InteractiveOnly = interactiveOnly,
            Candidates = result.Success
                ? DeserializeList<BrowserLocatorCandidate>(result.ValueJson)
                : []
        };
    }

    private static BrowserEvaluateValue CreateEvaluateValue(BrowserAutomationResult result, string pageId)
    {
        return new BrowserEvaluateValue
        {
            PageId = pageId,
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            Error = result.Error,
            ValueJson = result.Success ? result.ValueJson : null
        };
    }

    private static CallToolResult CreateScreenshotToolResult(
        BrowserAutomationResult result,
        string? format)
    {
        if (!result.Success)
        {
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = FormatScreenshotError(result)
                    }
                ]
            };
        }

        if (!TryDecodeScreenshotBytes(result.ScreenshotBase64, out var screenshotBytes))
        {
            return new CallToolResult
            {
                IsError = true,
                Content =
                [
                    new TextContentBlock
                    {
                        Text = "Screenshot capture returned invalid image data."
                    }
                ]
            };
        }

        return new CallToolResult
        {
            IsError = false,
            Content =
            [
                new ImageContentBlock
                {
                    MimeType = GetScreenshotMimeType(format),
                    Data = screenshotBytes
                }
            ]
        };
    }

    private static string FormatScreenshotError(BrowserAutomationResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.ErrorCode) && !string.IsNullOrWhiteSpace(result.Error))
        {
            return $"{result.ErrorCode}: {result.Error}";
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            return result.Error;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorCode))
        {
            return result.ErrorCode;
        }

        return "Screenshot capture failed.";
    }

    private static string GetScreenshotMimeType(string? format)
    {
        return format?.Trim().ToLowerInvariant() switch
        {
            "jpeg" or "jpg" => "image/jpeg",
            "webp" => "image/webp",
            _ => "image/png"
        };
    }

    private static bool TryDecodeScreenshotBytes(string? screenshotBase64, out ReadOnlyMemory<byte> screenshotBytes)
    {
        if (string.IsNullOrWhiteSpace(screenshotBase64))
        {
            screenshotBytes = default;
            return false;
        }

        try
        {
            screenshotBytes = Convert.FromBase64String(screenshotBase64);
            return true;
        }
        catch (FormatException)
        {
            screenshotBytes = default;
            return false;
        }
    }

    private static BrowserConsoleMessagesSnapshot CreateConsoleMessagesSnapshot(
        BrowserAutomationResult result,
        string pageId)
    {
        return new BrowserConsoleMessagesSnapshot
        {
            PageId = pageId,
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            Error = result.Error,
            Entries = result.Success
                ? DeserializeList<BrowserConsoleMessageEntry>(result.ValueJson)
                : []
        };
    }

    private static BrowserNetworkRequestsSnapshot CreateNetworkRequestsSnapshot(
        BrowserAutomationResult result,
        string pageId)
    {
        return new BrowserNetworkRequestsSnapshot
        {
            PageId = pageId,
            Success = result.Success,
            ErrorCode = result.ErrorCode,
            Error = result.Error,
            Entries = result.Success
                ? DeserializeList<BrowserNetworkRequestEntry>(result.ValueJson)
                : []
        };
    }
}
