using System.IO;
using BrowserCommanderServer;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class McpTransportDiagnosticsTests
{
    [Fact]
    public void ClassifyOutcome_ReturnsStaleSessionCandidate_For404WithSessionId()
    {
        var outcome = McpTransportDiagnostics.ClassifyOutcome(
            StatusCodes.Status404NotFound,
            requestSessionId: "session-123",
            exception: null,
            requestAborted: false);

        Assert.Equal(McpTransportDiagnostics.StaleSessionCandidate, outcome);
    }

    [Fact]
    public void ClassifyOutcome_ReturnsTunnelResetCandidate_ForAbortedRequest()
    {
        var outcome = McpTransportDiagnostics.ClassifyOutcome(
            StatusCodes.Status200OK,
            requestSessionId: null,
            exception: new OperationCanceledException(),
            requestAborted: true);

        Assert.Equal(McpTransportDiagnostics.TunnelResetCandidate, outcome);
    }

    [Fact]
    public void ClassifyOutcome_ReturnsTunnelResetCandidate_ForIoFailure()
    {
        var outcome = McpTransportDiagnostics.ClassifyOutcome(
            StatusCodes.Status200OK,
            requestSessionId: null,
            exception: new IOException("Connection dropped."),
            requestAborted: false);

        Assert.Equal(McpTransportDiagnostics.TunnelResetCandidate, outcome);
    }

    [Fact]
    public void ClassifyOutcome_ReturnsTransportTimeout_For408WithoutException()
    {
        var outcome = McpTransportDiagnostics.ClassifyOutcome(
            StatusCodes.Status408RequestTimeout,
            requestSessionId: null,
            exception: null,
            requestAborted: false);

        Assert.Equal(McpTransportDiagnostics.TransportTimeout, outcome);
    }

    [Fact]
    public void ClassifyOutcome_ReturnsTransportOk_ForSuccessfulRequest()
    {
        var outcome = McpTransportDiagnostics.ClassifyOutcome(
            StatusCodes.Status200OK,
            requestSessionId: null,
            exception: null,
            requestAborted: false);

        Assert.Equal(McpTransportDiagnostics.TransportOk, outcome);
    }
}
