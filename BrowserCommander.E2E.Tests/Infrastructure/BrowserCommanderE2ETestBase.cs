using Xunit;

namespace BrowserCommander.E2E.Tests.Infrastructure;

[Collection(BrowserCommanderE2ECollection.Name)]
public abstract class BrowserCommanderE2ETestBase
{
    protected BrowserCommanderE2ETestBase(BrowserCommanderE2EFixture fixture)
    {
        Fixture = fixture;
    }

    protected BrowserCommanderE2EFixture Fixture { get; }

    protected async Task ExecuteAsync(string testName, Func<BrowserCommanderE2ESession, Task> action)
    {
        await using var session = await Fixture.CreateSessionAsync(testName);

        try
        {
            await action(session);
        }
        catch (Exception exception)
        {
            await session.CaptureFailureArtifactsAsync(exception);
            throw;
        }
    }
}
