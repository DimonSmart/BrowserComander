using BrowserCommander.Contracts;

namespace BrowserCommander;

public sealed class BackgroundAgentStatus
{
    public bool Ok { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public bool Connected { get; set; }

    public List<BrowserTabDescriptor> AllowedTabs { get; set; } = [];

    public string ConnectionStatus { get; set; } = "Disconnected";

    public string SyncStatus { get; set; } = "Pending";

    public long StateRevision { get; set; }

    public long LastAcknowledgedRevision { get; set; } = -1;

    public string? ServerInstanceId { get; set; }

    public string? LastSyncAt { get; set; }

    public int AuthorizedTabCount { get; set; }

    public int AuthorizationLimit { get; set; } = 64;

    public string? ErrorCode { get; set; }

    public string Error { get; set; } = string.Empty;
}
