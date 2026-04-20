namespace BrowserCommander;

public sealed class GlobalPageInfo
{
    public string PageId { get; set; } = string.Empty;

    public string AgentId { get; set; } = string.Empty;

    public string? BrowserName { get; set; }

    public int TabId { get; set; }

    public bool Active { get; set; }

    public string? Title { get; set; }

    public string? Url { get; set; }
}

public sealed class GlobalPagesResult
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public List<GlobalPageInfo> Pages { get; set; } = [];
}
