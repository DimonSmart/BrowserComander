namespace BrowserCommander.Contracts;

public static class BrowserExecutionStepKinds
{
    public const string ContentScript = "contentScript";
    public const string Debugger = "debugger";
    public const string Tab = "tab";
}

public static class BrowserExecutionOperations
{
    public const string GetPageUrl = "getPageUrl";
    public const string GetPageTitle = "getPageTitle";
    public const string GetPageContent = "getPageContent";
    public const string FindLocators = "findLocators";
    public const string FillLocator = "fillLocator";
    public const string FocusLocator = "focusLocator";
    public const string ClickLocator = "clickLocator";
    public const string ReadInnerText = "readInnerText";
    public const string ReadTextContent = "readTextContent";
    public const string ReadInnerHtml = "readInnerHtml";
    public const string ReadInputValue = "readInputValue";
    public const string CheckExists = "checkExists";
    public const string CountMatches = "countMatches";
    public const string CheckVisible = "checkVisible";
    public const string WaitForLocator = "waitForLocator";
    public const string Evaluate = "evaluate";
    public const string PressKey = "pressKey";
    public const string CaptureScreenshot = "captureScreenshot";
    public const string ReadConsoleMessages = "readConsoleMessages";
    public const string ReadNetworkRequests = "readNetworkRequests";
    public const string Goto = "goto";
    public const string Reload = "reload";
    public const string GoBack = "goBack";
    public const string GoForward = "goForward";
    public const string WaitForUrl = "waitForUrl";
    public const string WaitForLoadState = "waitForLoadState";
}

public sealed class BrowserExecutionPlan
{
    public List<BrowserExecutionStep> Steps { get; set; } = [];
}

public sealed class BrowserExecutionStep
{
    public string Kind { get; set; } = string.Empty;

    public string Operation { get; set; } = string.Empty;

    public string? Selector { get; set; }

    public string? Text { get; set; }

    public string? Key { get; set; }

    public string? Url { get; set; }

    public string? MatchMode { get; set; }

    public string? WaitState { get; set; }

    public string? Script { get; set; }

    public string? Query { get; set; }

    public bool? OnlyVisible { get; set; }

    public bool? InteractiveOnly { get; set; }

    public string? Format { get; set; }

    public int? Limit { get; set; }

    public bool? ClearBuffer { get; set; }

    public int? TimeoutMs { get; set; }
}

public sealed class BrowserAgentCapabilities
{
    public bool SupportsPlanExecution { get; set; }

    public bool SupportsContentScriptSteps { get; set; }

    public bool SupportsDebuggerSteps { get; set; }

    public bool SupportsTabSteps { get; set; }
}
