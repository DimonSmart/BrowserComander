namespace BrowserCommander.McpStdioBridge;

public sealed class StdioBridgeOptions
{
    public required Uri UpstreamEndpoint { get; init; }

    public string? RepositoryRootPath { get; init; }

    public string? LocalServerProjectPath { get; init; }

    public string? PublishedServerExecutablePath { get; init; }

    public bool CanAutoStartLocalServer =>
        (!string.IsNullOrWhiteSpace(PublishedServerExecutablePath)
         || !string.IsNullOrWhiteSpace(LocalServerProjectPath))
        && IsSupportedLocalEndpoint(UpstreamEndpoint);

    public string UpstreamServerBaseAddress =>
        new UriBuilder(UpstreamEndpoint)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri.GetLeftPart(UriPartial.Authority);

    private static bool IsSupportedLocalEndpoint(Uri endpoint)
    {
        if (!endpoint.IsLoopback)
        {
            return false;
        }

        var path = endpoint.AbsolutePath.TrimEnd('/');
        return string.Equals(path, "/mcp", StringComparison.OrdinalIgnoreCase);
    }
}
