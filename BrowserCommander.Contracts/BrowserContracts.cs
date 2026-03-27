namespace BrowserCommander.Contracts;

public static class BrowserCommandActions
{
    public const string ExecutePlan = "executePlan";
    public const string PageUrl = "pageUrl";
    public const string PageTitle = "pageTitle";
    public const string PageContent = "pageContent";
    public const string PageFindLocators = "pageFindLocators";
    public const string PageEvaluate = "pageEvaluate";
    public const string PageScreenshot = "pageScreenshot";
    public const string PageConsoleMessages = "pageConsoleMessages";
    public const string PageNetworkRequests = "pageNetworkRequests";
    public const string PageGoto = "pageGoto";
    public const string PageReload = "pageReload";
    public const string PageGoBack = "pageGoBack";
    public const string PageGoForward = "pageGoForward";
    public const string PageWaitForUrl = "pageWaitForUrl";
    public const string PageWaitForLoadState = "pageWaitForLoadState";

    public const string LocatorClick = "locatorClick";
    public const string LocatorFill = "locatorFill";
    public const string LocatorPress = "locatorPress";
    public const string LocatorInnerText = "locatorInnerText";
    public const string LocatorTextContent = "locatorTextContent";
    public const string LocatorInnerHtml = "locatorInnerHtml";
    public const string LocatorInputValue = "locatorInputValue";
    public const string LocatorExists = "locatorExists";
    public const string LocatorCount = "locatorCount";
    public const string LocatorIsVisible = "locatorIsVisible";
    public const string LocatorWaitFor = "locatorWaitFor";

    public const string SetText = "setText";
    public const string GetText = "getText";
    public const string GetHtml = "getHtml";
    public const string Click = "click";
    public const string Exists = "exists";
}

public static class BrowserCommandErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string AgentNotFound = "agent_not_found";
    public const string AgentDisconnected = "agent_disconnected";
    public const string Timeout = "timeout";
    public const string UnsupportedAction = "unsupported_action";
    public const string UnsupportedWaitState = "unsupported_wait_state";
    public const string UnsupportedMatchMode = "unsupported_match_mode";
    public const string ContentScriptUnavailable = "content_script_unavailable";
    public const string ElementNotFound = "element_not_found";
    public const string ElementNotVisible = "element_not_visible";
    public const string ElementNotEditable = "element_not_editable";
    public const string TabNotAuthorized = "tab_not_authorized";
    public const string TabNotFound = "tab_not_found";
    public const string ExecutionFailed = "execution_failed";
}

public static class BrowserCommanderHubMethods
{
    public const string RegisterAgent = "RegisterAgent";
    public const string UpdateTabs = "UpdateTabs";
    public const string CompleteCommand = "CompleteCommand";
    public const string ExecuteCommand = "ExecuteCommand";
}

public sealed class BrowserAutomationCommand
{
    public string CommandId { get; set; } = Guid.NewGuid().ToString("N");

    public string AgentId { get; set; } = string.Empty;

    public int TabId { get; set; }

    public int? FrameId { get; set; }

    public string Action { get; set; } = string.Empty;

    public BrowserExecutionPlan? Plan { get; set; }

    public string? Selector { get; set; }

    public string? Text { get; set; }

    public string? Key { get; set; }

    public string? Url { get; set; }

    public string? MatchMode { get; set; }

    public string? WaitState { get; set; }

    public string? Script { get; set; }

    public string? Query { get; set; }

    public bool OnlyVisible { get; set; } = true;

    public bool InteractiveOnly { get; set; } = true;

    public string? Format { get; set; }

    public int? Limit { get; set; }

    public bool ClearBuffer { get; set; }

    public int TimeoutMs { get; set; } = 10000;
}

public sealed class BrowserAutomationResult
{
    public string CommandId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public int TabId { get; set; }

    public string Action { get; set; } = string.Empty;

    public bool Success { get; set; }

    public string? Text { get; set; }

    public string? Html { get; set; }

    public bool? Exists { get; set; }

    public string? Url { get; set; }

    public string? Title { get; set; }

    public int? Count { get; set; }

    public bool? Visible { get; set; }

    public string? ReadyState { get; set; }

    public string? ValueJson { get; set; }

    public string? ScreenshotBase64 { get; set; }

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }
}

public sealed class BrowserTabDescriptor
{
    public int TabId { get; set; }

    public int WindowId { get; set; }

    public bool Active { get; set; }

    public string? Url { get; set; }

    public string? Title { get; set; }
}

public sealed class BrowserAgentRegistration
{
    public string AgentId { get; set; } = string.Empty;

    public string? ExtensionId { get; set; }

    public string? BrowserName { get; set; }

    public string? UserAgent { get; set; }

    public string? ProtocolVersion { get; set; }

    public BrowserAgentCapabilities Capabilities { get; set; } = new();

    public List<BrowserTabDescriptor> Tabs { get; set; } = [];
}

public sealed class BrowserAgentTabsUpdate
{
    public string AgentId { get; set; } = string.Empty;

    public List<BrowserTabDescriptor> Tabs { get; set; } = [];
}
