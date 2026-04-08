using BrowserCommander.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace BrowserCommanderServer.Controllers;

[ApiController]
[Route("api/browser-automation")]
public class BrowserAutomationController : ControllerBase
{
    private readonly IBrowserAutomationService _browserAutomationService;

    public BrowserAutomationController(IBrowserAutomationService browserAutomationService)
    {
        _browserAutomationService = browserAutomationService;
    }

    [HttpGet("agents")]
    public ActionResult<IReadOnlyCollection<BrowserAgentStatus>> GetAgents()
    {
        return Ok(_browserAutomationService.GetAgents());
    }

    [HttpGet("pages")]
    public ActionResult<IReadOnlyCollection<BrowserPageSummary>> GetPages()
    {
        return Ok(_browserAutomationService.GetPages());
    }

    [HttpPost("commands")]
    public async Task<ActionResult<BrowserAutomationResult>> ExecuteCommand(
        [FromBody] BrowserAutomationCommand command,
        CancellationToken cancellationToken)
    {
        var validationError = ValidateCommand(command);
        if (validationError is not null)
        {
            return BadRequest(new BrowserAutomationResult
            {
                CommandId = command.CommandId,
                AgentId = command.AgentId,
                TabId = command.TabId,
                Action = command.Action,
                Success = false,
                ErrorCode = BrowserCommandErrorCodes.ValidationFailed,
                Error = validationError
            });
        }

        var result = await _browserAutomationService.ExecuteCommandAsync(command, cancellationToken);
        return ToHttpResult(result);
    }

    [HttpPost("set-text")]
    public Task<ActionResult<BrowserAutomationResult>> SetText(
        [FromBody] SetTextCommandRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteCommand(
            new BrowserAutomationCommand
            {
                AgentId = request.AgentId,
                TabId = request.TabId,
                FrameId = request.FrameId,
                Action = BrowserCommandActions.SetText,
                Selector = request.Selector,
                Text = request.Text,
                TimeoutMs = request.TimeoutMs
            },
            cancellationToken);
    }

    [HttpPost("get-text")]
    public Task<ActionResult<BrowserAutomationResult>> GetText(
        [FromBody] GetTextCommandRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteCommand(
            new BrowserAutomationCommand
            {
                AgentId = request.AgentId,
                TabId = request.TabId,
                FrameId = request.FrameId,
                Action = BrowserCommandActions.GetText,
                Selector = request.Selector,
                TimeoutMs = request.TimeoutMs
            },
            cancellationToken);
    }

    [HttpPost("get-html")]
    public Task<ActionResult<BrowserAutomationResult>> GetHtml(
        [FromBody] GetHtmlCommandRequest request,
        CancellationToken cancellationToken)
    {
        return ExecuteCommand(
            new BrowserAutomationCommand
            {
                AgentId = request.AgentId,
                TabId = request.TabId,
                FrameId = request.FrameId,
                Action = BrowserCommandActions.GetHtml,
                Selector = string.IsNullOrWhiteSpace(request.Selector) ? "html" : request.Selector,
                TimeoutMs = request.TimeoutMs
            },
            cancellationToken);
    }

    private ActionResult<BrowserAutomationResult> ToHttpResult(BrowserAutomationResult result)
    {
        if (result.Success)
        {
            return Ok(result);
        }

        return result.ErrorCode switch
        {
            BrowserCommandErrorCodes.AgentNotFound or BrowserCommandErrorCodes.AgentDisconnected => NotFound(result),
            BrowserCommandErrorCodes.TabNotAuthorized => StatusCode(StatusCodes.Status403Forbidden, result),
            BrowserCommandErrorCodes.Timeout => StatusCode(StatusCodes.Status504GatewayTimeout, result),
            BrowserCommandErrorCodes.ValidationFailed => BadRequest(result),
            _ => StatusCode(StatusCodes.Status502BadGateway, result)
        };
    }

    private static string? ValidateCommand(BrowserAutomationCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.AgentId))
        {
            return "AgentId is required.";
        }

        if (command.TabId <= 0)
        {
            return "TabId must be greater than zero.";
        }

        if (string.IsNullOrWhiteSpace(command.Action))
        {
            return "Action is required.";
        }

        var action = command.Action.Trim();
        if (action.Equals(BrowserCommandActions.ExecutePlan, StringComparison.OrdinalIgnoreCase))
        {
            return command.Plan?.Steps?.Count > 0
                ? null
                : "Plan with at least one step is required for executePlan.";
        }

        if (RequiresSelector(action) && string.IsNullOrWhiteSpace(command.Selector))
        {
            return "Selector is required.";
        }

        if (RequiresText(action) && string.IsNullOrEmpty(command.Text))
        {
            return "Text is required for this action.";
        }

        if (RequiresKey(action) && string.IsNullOrWhiteSpace(command.Key))
        {
            return "Key is required for this action.";
        }

        if (RequiresUrl(action) && string.IsNullOrWhiteSpace(command.Url))
        {
            return "Url is required for this action.";
        }

        if (RequiresScript(action) && string.IsNullOrWhiteSpace(command.Script))
        {
            return "Script is required for this action.";
        }

        if (RequiresViewportSize(action))
        {
            if (command.Width is null or <= 0)
            {
                return "Width must be a positive integer for this action.";
            }

            if (command.Height is null or <= 0)
            {
                return "Height must be a positive integer for this action.";
            }
        }

        return IsSupportedAction(action)
            ? null
            : $"Unsupported action '{command.Action}'.";
    }

    private static bool IsSupportedAction(string action)
    {
        return action.Equals(BrowserCommandActions.PageUrl, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.ExecutePlan, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageTitle, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageContent, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageFindLocators, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageEvaluate, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageScreenshot, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageConsoleMessages, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageNetworkRequests, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageGoto, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageReload, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageGoBack, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageGoForward, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageWaitForUrl, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageWaitForLoadState, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageSetViewportSize, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageClearViewportOverride, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorClick, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorFill, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorPress, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorInnerText, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorTextContent, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorInnerHtml, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorInputValue, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorExists, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorCount, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorIsVisible, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorWaitFor, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.SetText, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.GetText, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.GetHtml, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.Click, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.Exists, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresSelector(string action)
    {
        return action.Equals(BrowserCommandActions.LocatorClick, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorFill, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorPress, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorInnerText, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorTextContent, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorInnerHtml, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorInputValue, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorExists, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorCount, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorIsVisible, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.LocatorWaitFor, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.SetText, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.GetText, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.GetHtml, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.Click, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.Exists, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresText(string action)
    {
        return action.Equals(BrowserCommandActions.LocatorFill, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.SetText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresKey(string action)
    {
        return action.Equals(BrowserCommandActions.LocatorPress, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresUrl(string action)
    {
        return action.Equals(BrowserCommandActions.PageGoto, StringComparison.OrdinalIgnoreCase)
               || action.Equals(BrowserCommandActions.PageWaitForUrl, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresScript(string action)
    {
        return action.Equals(BrowserCommandActions.PageEvaluate, StringComparison.OrdinalIgnoreCase);
    }

    private static bool RequiresViewportSize(string action)
    {
        return action.Equals(BrowserCommandActions.PageSetViewportSize, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class SetTextCommandRequest
    {
        public string AgentId { get; set; } = string.Empty;

        public int TabId { get; set; }

        public int? FrameId { get; set; }

        public string Selector { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public int TimeoutMs { get; set; } = 10000;
    }

    public sealed class GetTextCommandRequest
    {
        public string AgentId { get; set; } = string.Empty;

        public int TabId { get; set; }

        public int? FrameId { get; set; }

        public string Selector { get; set; } = string.Empty;

        public int TimeoutMs { get; set; } = 10000;
    }

    public sealed class GetHtmlCommandRequest
    {
        public string AgentId { get; set; } = string.Empty;

        public int TabId { get; set; }

        public int? FrameId { get; set; }

        public string Selector { get; set; } = "html";

        public int TimeoutMs { get; set; } = 10000;
    }
}
