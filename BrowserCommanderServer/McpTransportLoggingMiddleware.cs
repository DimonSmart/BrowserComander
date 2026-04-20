using System.Diagnostics;

namespace BrowserCommanderServer;

public sealed class McpTransportLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<McpTransportLoggingMiddleware> _logger;

    public McpTransportLoggingMiddleware(
        RequestDelegate next,
        ILogger<McpTransportLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        var requestSessionId = NormalizeHeader(context.Request.Headers["MCP-Session-Id"]);
        var lastEventId = NormalizeHeader(context.Request.Headers["Last-Event-ID"]);
        var userAgent = NormalizeHeader(context.Request.Headers.UserAgent);
        var forwardedFor = NormalizeHeader(context.Request.Headers["X-Forwarded-For"]);
        var forwardedHost = NormalizeHeader(context.Request.Headers["X-Forwarded-Host"]);
        var forwardedProto = NormalizeHeader(context.Request.Headers["X-Forwarded-Proto"]);

        _logger.LogInformation(
            "MCP request started. Method={Method}, Path={Path}, QueryString={QueryString}, TraceId={TraceId}, SessionId={SessionId}, LastEventId={LastEventId}, UserAgent={UserAgent}, ForwardedFor={ForwardedFor}, ForwardedHost={ForwardedHost}, ForwardedProto={ForwardedProto}.",
            context.Request.Method,
            context.Request.Path.Value ?? "/mcp",
            context.Request.QueryString.Value ?? string.Empty,
            traceId,
            requestSessionId,
            lastEventId,
            userAgent,
            forwardedFor,
            forwardedHost,
            forwardedProto);

        Exception? failure = null;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            failure = ex;
            throw;
        }
        finally
        {
            var responseSessionId = NormalizeHeader(context.Response.Headers["MCP-Session-Id"]);
            var outcome = McpTransportDiagnostics.ClassifyOutcome(
                context.Response.StatusCode,
                requestSessionId,
                failure,
                context.RequestAborted.IsCancellationRequested);
            var outcomeHint = McpTransportDiagnostics.DescribeOutcome(outcome);

            var logLevel = failure is not null
                || outcome != McpTransportDiagnostics.TransportOk
                || context.Response.StatusCode >= StatusCodes.Status400BadRequest
                ? LogLevel.Warning
                : LogLevel.Information;

            _logger.Log(
                logLevel,
                failure,
                "MCP request completed. Method={Method}, Path={Path}, TraceId={TraceId}, StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, Outcome={Outcome}, OutcomeHint={OutcomeHint}, SessionId={SessionId}, ResponseSessionId={ResponseSessionId}.",
                context.Request.Method,
                context.Request.Path.Value ?? "/mcp",
                traceId,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                outcome,
                outcomeHint ?? string.Empty,
                requestSessionId,
                responseSessionId);
        }
    }

    private static string NormalizeHeader(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value;
    }
}
