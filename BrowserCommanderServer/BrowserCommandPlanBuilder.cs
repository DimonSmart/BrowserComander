using BrowserCommander.Contracts;

namespace BrowserCommanderServer;

public static class BrowserCommandPlanBuilder
{
    public static BrowserExecutionPlan PageUrl() =>
        TabPlan(BrowserExecutionOperations.GetPageUrl);

    public static BrowserExecutionPlan PageTitle() =>
        TabPlan(BrowserExecutionOperations.GetPageTitle);

    public static BrowserExecutionPlan PageContent() =>
        ContentPlan(BrowserExecutionOperations.GetPageContent);

    public static BrowserExecutionPlan PageFindLocators(
        string? query,
        bool onlyVisible,
        bool interactiveOnly,
        int limit) =>
        ContentPlan(
            BrowserExecutionOperations.FindLocators,
            step =>
            {
                step.Query = query;
                step.OnlyVisible = onlyVisible;
                step.InteractiveOnly = interactiveOnly;
                step.Limit = limit;
            });

    public static BrowserExecutionPlan PageEvaluate(string expression) =>
        DebuggerPlan(
            BrowserExecutionOperations.Evaluate,
            step => step.Script = expression);

    public static BrowserExecutionPlan PageScreenshot(string? format) =>
        DebuggerPlan(
            BrowserExecutionOperations.CaptureScreenshot,
            step => step.Format = format);

    public static BrowserExecutionPlan PageConsoleMessages(int limit, bool clearBuffer) =>
        DebuggerPlan(
            BrowserExecutionOperations.ReadConsoleMessages,
            step =>
            {
                step.Limit = limit;
                step.ClearBuffer = clearBuffer;
            });

    public static BrowserExecutionPlan PageNetworkRequests(int limit, bool clearBuffer) =>
        DebuggerPlan(
            BrowserExecutionOperations.ReadNetworkRequests,
            step =>
            {
                step.Limit = limit;
                step.ClearBuffer = clearBuffer;
            });

    public static BrowserExecutionPlan PageGoto(string url, string waitState) =>
        TabPlan(
            BrowserExecutionOperations.Goto,
            step =>
            {
                step.Url = url;
                step.WaitState = waitState;
            });

    public static BrowserExecutionPlan PageReload(string waitState) =>
        TabPlan(
            BrowserExecutionOperations.Reload,
            step => step.WaitState = waitState);

    public static BrowserExecutionPlan PageGoBack(string waitState) =>
        TabPlan(
            BrowserExecutionOperations.GoBack,
            step => step.WaitState = waitState);

    public static BrowserExecutionPlan PageGoForward(string waitState) =>
        TabPlan(
            BrowserExecutionOperations.GoForward,
            step => step.WaitState = waitState);

    public static BrowserExecutionPlan PageWaitForUrl(string url, string matchMode) =>
        TabPlan(
            BrowserExecutionOperations.WaitForUrl,
            step =>
            {
                step.Url = url;
                step.MatchMode = matchMode;
            });

    public static BrowserExecutionPlan PageWaitForLoadState(string waitState) =>
        TabPlan(
            BrowserExecutionOperations.WaitForLoadState,
            step => step.WaitState = waitState);

    public static BrowserExecutionPlan LocatorClick(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.ClickLocator,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorFill(string selector, string value) =>
        ContentPlan(
            BrowserExecutionOperations.FillLocator,
            step =>
            {
                step.Selector = selector;
                step.Text = value;
            });

    public static BrowserExecutionPlan LocatorPress(string selector, string key) =>
        new()
        {
            Steps =
            [
                new BrowserExecutionStep
                {
                    Kind = BrowserExecutionStepKinds.ContentScript,
                    Operation = BrowserExecutionOperations.FocusLocator,
                    Selector = selector
                },
                new BrowserExecutionStep
                {
                    Kind = BrowserExecutionStepKinds.Debugger,
                    Operation = BrowserExecutionOperations.PressKey,
                    Key = key
                }
            ]
        };

    public static BrowserExecutionPlan LocatorInnerText(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.ReadInnerText,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorTextContent(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.ReadTextContent,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorInnerHtml(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.ReadInnerHtml,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorInputValue(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.ReadInputValue,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorExists(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.CheckExists,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorCount(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.CountMatches,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorIsVisible(string selector) =>
        ContentPlan(
            BrowserExecutionOperations.CheckVisible,
            step => step.Selector = selector);

    public static BrowserExecutionPlan LocatorWaitFor(string selector, string waitState) =>
        ContentPlan(
            BrowserExecutionOperations.WaitForLocator,
            step =>
            {
                step.Selector = selector;
                step.WaitState = waitState;
            });

    private static BrowserExecutionPlan ContentPlan(
        string operation,
        Action<BrowserExecutionStep>? configure = null) =>
        SingleStepPlan(BrowserExecutionStepKinds.ContentScript, operation, configure);

    private static BrowserExecutionPlan DebuggerPlan(
        string operation,
        Action<BrowserExecutionStep>? configure = null) =>
        SingleStepPlan(BrowserExecutionStepKinds.Debugger, operation, configure);

    private static BrowserExecutionPlan TabPlan(
        string operation,
        Action<BrowserExecutionStep>? configure = null) =>
        SingleStepPlan(BrowserExecutionStepKinds.Tab, operation, configure);

    private static BrowserExecutionPlan SingleStepPlan(
        string kind,
        string operation,
        Action<BrowserExecutionStep>? configure)
    {
        var step = new BrowserExecutionStep
        {
            Kind = kind,
            Operation = operation
        };

        configure?.Invoke(step);

        return new BrowserExecutionPlan
        {
            Steps = [step]
        };
    }
}
