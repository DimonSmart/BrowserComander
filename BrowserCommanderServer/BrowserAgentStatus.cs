using BrowserCommander.Contracts;

namespace BrowserCommanderServer;

public sealed class BrowserAgentStatus
{
    public string AgentId { get; set; } = string.Empty;

    public string ReportedAgentId { get; set; } = string.Empty;

    public string ConnectionId { get; set; } = string.Empty;

    public string? ExtensionId { get; set; }

    public string? BrowserName { get; set; }

    public string? UserAgent { get; set; }

    public string? ProtocolVersion { get; set; }

    public int DefaultCommandTimeoutMs { get; set; } = BrowserCommandDefaults.TimeoutMs;

    public BrowserAgentCapabilities Capabilities { get; set; } = new();

    public DateTimeOffset ConnectedAtUtc { get; set; }

    public DateTimeOffset LastSeenAtUtc { get; set; }

    public List<BrowserTabDescriptor> Tabs { get; set; } = [];
}
