namespace BrowserCommanderServer;

public sealed class BrowserPageSummary
{
    public string PageId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public int TabId { get; set; }

    public int WindowId { get; set; }

    public bool Active { get; set; }

    public string? Title { get; set; }

    public string? Url { get; set; }
}
