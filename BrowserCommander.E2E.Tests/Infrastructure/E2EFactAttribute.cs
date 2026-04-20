using Xunit;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (!BrowserCommanderE2EEnvironment.IsEnabled)
        {
            Skip = "Set BROWSER_COMMANDER_RUN_E2E=1 to run BrowserCommander browser end-to-end tests.";
        }
    }
}
