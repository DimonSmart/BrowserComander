using BrowserCommander.Contracts;

namespace BrowserCommander;

public sealed class BackgroundAgentStatus
{
    public bool Ok { get; set; }

    public string AgentId { get; set; } = string.Empty;

    public bool Connected { get; set; }

    public List<BrowserTabDescriptor> AllowedTabs { get; set; } = [];

    public string Error { get; set; } = string.Empty;
}
