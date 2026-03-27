namespace BrowserCommanderServer;

public sealed class BrowserConsoleMessageEntry
{
    public string? Source { get; set; }

    public string? Level { get; set; }

    public string? Type { get; set; }

    public string? Text { get; set; }

    public string? Url { get; set; }

    public double? Timestamp { get; set; }
}

public sealed class BrowserConsoleMessagesSnapshot
{
    public string PageId { get; set; } = string.Empty;

    public bool Success { get; set; } = true;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public List<BrowserConsoleMessageEntry> Entries { get; set; } = [];
}

public sealed class BrowserNetworkRequestEntry
{
    public string? RequestId { get; set; }

    public string? Url { get; set; }

    public string? Method { get; set; }

    public string? ResourceType { get; set; }

    public int? Status { get; set; }

    public string? StatusText { get; set; }

    public string? MimeType { get; set; }

    public bool Failed { get; set; }

    public string? ErrorText { get; set; }

    public double? StartedAt { get; set; }

    public double? ResponseAt { get; set; }

    public double? FinishedAt { get; set; }
}

public sealed class BrowserNetworkRequestsSnapshot
{
    public string PageId { get; set; } = string.Empty;

    public bool Success { get; set; } = true;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public List<BrowserNetworkRequestEntry> Entries { get; set; } = [];
}

public sealed class BrowserEvaluateValue
{
    public string PageId { get; set; } = string.Empty;

    public bool Success { get; set; } = true;

    public string? ErrorCode { get; set; }

    public string? Error { get; set; }

    public string? ValueJson { get; set; }
}
