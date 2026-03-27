namespace BrowserCommanderServer;

public sealed class BrowserLocatorCandidate
{
    public string Selector { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public string? Id { get; set; }

    public string? Role { get; set; }

    public string? Type { get; set; }

    public string? Name { get; set; }

    public string? Placeholder { get; set; }

    public string? AriaLabel { get; set; }

    public string? Title { get; set; }

    public string? Text { get; set; }

    public bool Visible { get; set; }

    public bool Editable { get; set; }

    public bool Disabled { get; set; }

    public int Score { get; set; }

    public List<string> MatchedFields { get; set; } = [];
}

public sealed class BrowserLocatorSearchResult
{
    public string PageId { get; set; } = string.Empty;

    public bool Success { get; set; } = true;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public string Query { get; set; } = string.Empty;

    public bool OnlyVisible { get; set; }

    public bool InteractiveOnly { get; set; }

    public List<BrowserLocatorCandidate> Candidates { get; set; } = [];
}
