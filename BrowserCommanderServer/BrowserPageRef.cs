namespace BrowserCommanderServer;

public readonly record struct BrowserPageRef(string AgentId, int TabId)
{
    private const string Prefix = "page";

    public string PageId => CreatePageId(AgentId, TabId);

    public static string CreatePageId(string agentId, int tabId)
    {
        return $"{Prefix}:{Uri.EscapeDataString(agentId)}:{tabId}";
    }

    public static bool TryParse(string? pageId, out BrowserPageRef pageRef)
    {
        pageRef = default;

        if (string.IsNullOrWhiteSpace(pageId))
        {
            return false;
        }

        var parts = pageId.Split(':', StringSplitOptions.None);
        if (parts.Length != 3
            || !parts[0].Equals(Prefix, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[1])
            || !int.TryParse(parts[2], out var tabId)
            || tabId <= 0)
        {
            return false;
        }

        pageRef = new BrowserPageRef(Uri.UnescapeDataString(parts[1]), tabId);
        return true;
    }
}
