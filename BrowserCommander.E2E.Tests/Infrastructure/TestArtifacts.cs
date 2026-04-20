using Microsoft.Playwright;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class TestArtifacts
{
    public TestArtifacts(string testName)
    {
        DirectoryPath = Path.Combine(
            BrowserCommanderE2EEnvironment.ArtifactsRootPath,
            SanitizePathSegment(testName));

        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public string GetPath(string fileName)
    {
        return Path.Combine(DirectoryPath, fileName);
    }

    public async Task CaptureScreenshotAsync(IPage page, string fileName, CancellationToken cancellationToken = default)
    {
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = GetPath(fileName),
            FullPage = true,
            Timeout = 15_000
        });
    }

    public Task WriteTextAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        return File.WriteAllTextAsync(GetPath(fileName), content, cancellationToken);
    }

    private static string SanitizePathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed-test" : sanitized;
    }
}
