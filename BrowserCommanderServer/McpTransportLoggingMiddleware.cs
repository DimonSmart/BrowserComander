using System.Diagnostics;
using System.Text.Json;

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
        var requestSessionId = NormalizeValue(context.Request.Headers["MCP-Session-Id"]);
        var protocolVersion = NormalizeValue(context.Request.Headers["MCP-Protocol-Version"]);
        var lastEventId = NormalizeValue(context.Request.Headers["Last-Event-ID"]);
        var accept = NormalizeValue(context.Request.Headers["Accept"]);
        var requestContentType = NormalizeValue(context.Request.ContentType);
        var requestContentLength = NormalizeContentLength(context.Request.ContentLength);
        var userAgent = NormalizeValue(context.Request.Headers.UserAgent);
        var forwardedFor = NormalizeValue(context.Request.Headers["X-Forwarded-For"]);
        var forwardedHost = NormalizeValue(context.Request.Headers["X-Forwarded-Host"]);
        var forwardedProto = NormalizeValue(context.Request.Headers["X-Forwarded-Proto"]);
        var (mcpMethod, mcpName) = await ReadMcpRequestMetadataAsync(context.Request, context.RequestAborted);

        _logger.LogInformation(
            "MCP request started. Method={Method}, Path={Path}, QueryString={QueryString}, TraceId={TraceId}, SessionId={SessionId}, MCP-Protocol-Version={McpProtocolVersion}, Mcp-Method={McpMethod}, Mcp-Name={McpName}, Accept={Accept}, Request.Content-Type={RequestContentType}, Request.Content-Length={RequestContentLength}, LastEventId={LastEventId}, UserAgent={UserAgent}, ForwardedFor={ForwardedFor}, ForwardedHost={ForwardedHost}, ForwardedProto={ForwardedProto}.",
            context.Request.Method,
            context.Request.Path.Value ?? "/mcp",
            context.Request.QueryString.Value ?? string.Empty,
            traceId,
            requestSessionId,
            protocolVersion,
            mcpMethod,
            mcpName,
            accept,
            requestContentType,
            requestContentLength,
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
            var responseSessionId = NormalizeValue(context.Response.Headers["MCP-Session-Id"]);
            var responseContentType = NormalizeValue(context.Response.ContentType);
            var responseContentLength = NormalizeContentLength(context.Response.ContentLength);
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
                "MCP request completed. Method={Method}, Path={Path}, TraceId={TraceId}, StatusCode={StatusCode}, ElapsedMs={ElapsedMs}, Outcome={Outcome}, OutcomeHint={OutcomeHint}, SessionId={SessionId}, ResponseSessionId={ResponseSessionId}, MCP-Protocol-Version={McpProtocolVersion}, Mcp-Method={McpMethod}, Mcp-Name={McpName}, Accept={Accept}, Request.Content-Type={RequestContentType}, Request.Content-Length={RequestContentLength}, Response.Content-Type={ResponseContentType}, Response.Content-Length={ResponseContentLength}.",
                context.Request.Method,
                context.Request.Path.Value ?? "/mcp",
                traceId,
                context.Response.StatusCode,
                stopwatch.ElapsedMilliseconds,
                outcome,
                outcomeHint ?? string.Empty,
                requestSessionId,
                responseSessionId,
                protocolVersion,
                mcpMethod,
                mcpName,
                accept,
                requestContentType,
                requestContentLength,
                responseContentType,
                responseContentLength);
        }
    }

    private static async Task<(string Method, string Name)> ReadMcpRequestMetadataAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpMethods.IsPost(request.Method)
            || string.IsNullOrWhiteSpace(request.ContentType)
            || !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return ("-", "-");
        }

        try
        {
            request.EnableBuffering();
            request.Body.Position = 0;

            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            var root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return ("-", "-");
            }

            var method = root.TryGetProperty("method", out var methodElement)
                && methodElement.ValueKind == JsonValueKind.String
                    ? NormalizeValue(methodElement.GetString())
                    : "-";

            var name = root.TryGetProperty("params", out var paramsElement)
                && paramsElement.ValueKind == JsonValueKind.Object
                && paramsElement.TryGetProperty("name", out var nameElement)
                && nameElement.ValueKind == JsonValueKind.String
                    ? NormalizeValue(nameElement.GetString())
                    : "-";

            return (method, name);
        }
        catch (JsonException)
        {
            return ("-", "-");
        }
        finally
        {
            if (request.Body.CanSeek)
            {
                request.Body.Position = 0;
            }
        }
    }

    private static string NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "-"
            : value;
    }

    private static string NormalizeContentLength(long? value)
    {
        return value?.ToString() ?? "-";
    }
}
