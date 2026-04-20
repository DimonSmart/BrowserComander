using System.IO;

namespace BrowserCommanderServer;

internal static class McpTransportDiagnostics
{
    public const string TransportOk = "transport_ok";
    public const string StaleSessionCandidate = "stale_session_candidate";
    public const string TunnelResetCandidate = "tunnel_reset_candidate";
    public const string TransportTimeout = "transport_timeout";
    public const string TransportError = "transport_error";

    public static string ClassifyOutcome(
        int statusCode,
        string? requestSessionId,
        Exception? exception,
        bool requestAborted)
    {
        if (exception is not null)
        {
            if (requestAborted
                || exception is OperationCanceledException
                || exception is IOException)
            {
                return TunnelResetCandidate;
            }

            return TransportError;
        }

        if (statusCode == StatusCodes.Status404NotFound
            && !string.IsNullOrWhiteSpace(requestSessionId))
        {
            return StaleSessionCandidate;
        }

        if (statusCode == StatusCodes.Status408RequestTimeout)
        {
            return TransportTimeout;
        }

        return TransportOk;
    }

    public static string? DescribeOutcome(string outcome)
    {
        return outcome switch
        {
            StaleSessionCandidate =>
                "404 on /mcp with an incoming MCP-Session-Id. Usually means the client reused stale MCP HTTP session state or an old tunnel URL.",
            TunnelResetCandidate =>
                "The request ended with an abort/cancel or I/O failure before MCP completed. Usually indicates a tunnel reset, client disconnect, or proxy interruption.",
            TransportTimeout =>
                "The HTTP request reached the server but timed out before MCP completed.",
            TransportError =>
                "The server hit an unexpected transport-level failure while handling the MCP request.",
            _ => null
        };
    }
}
