namespace BrowserCommanderServer;

public sealed record BrowserConnectionStatus(
    string ServerInstanceId,
    string ServerStatus,
    int ConfirmedAgentCount,
    int AuthorizedTabCount,
    IReadOnlyList<BrowserConnectionAgentStatus> Agents);

public sealed record BrowserConnectionAgentStatus(
    string AgentId,
    string? BrowserName,
    string? ExtensionId,
    long AcknowledgedRevision,
    long ConnectionGeneration,
    int AuthorizedTabCount,
    DateTimeOffset LastSynchronizedAtUtc);
