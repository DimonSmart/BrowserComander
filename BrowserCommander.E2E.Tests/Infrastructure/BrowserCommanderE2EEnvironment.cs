namespace BrowserCommander.E2E.Tests.Infrastructure;

internal static class BrowserCommanderE2EEnvironment
{
    private const string RunFlag = "BROWSER_COMMANDER_RUN_E2E";
    private const string HeadlessFlag = "BROWSER_COMMANDER_E2E_HEADLESS";

    private static readonly Lazy<string> RepositoryRootPathValue = new(ResolveRepositoryRootPath);

    public static bool IsEnabled => IsTruthy(Environment.GetEnvironmentVariable(RunFlag));

    public static bool IsHeadless => IsTruthy(Environment.GetEnvironmentVariable(HeadlessFlag));

    public static string RepositoryRootPath => RepositoryRootPathValue.Value;

    public static string ExtensionPath =>
        Path.Combine(
            RepositoryRootPath,
            "BrowserCommander",
            "bin",
            "Debug",
            "net8.0",
            "browserextension");

    public static string TestSiteRootPath =>
        Path.Combine(RepositoryRootPath, "BrowserCommander.E2E.Tests", "TestSite");

    public static string ArtifactsRootPath =>
        Path.Combine(RepositoryRootPath, "artifacts", "e2e");

    public static void EnsureExtensionIsBuilt()
    {
        var manifestPath = Path.Combine(ExtensionPath, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Browser extension output was not found at '{ExtensionPath}'. Build BrowserCommander before running e2e tests.");
        }
    }

    private static bool IsTruthy(string? value)
    {
        return value is not null
               && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                   || value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveRepositoryRootPath()
    {
        var currentPath = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(currentPath))
        {
            var solutionPath = Path.Combine(currentPath, "BrowserComander.sln");
            if (File.Exists(solutionPath))
            {
                return currentPath;
            }

            currentPath = Directory.GetParent(currentPath)?.FullName ?? string.Empty;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate repository root from '{AppContext.BaseDirectory}'.");
    }
}
