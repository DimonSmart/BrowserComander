namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class HeadedOnlyE2EFactAttribute : E2EFactAttribute
{
    public HeadedOnlyE2EFactAttribute()
    {
        if (BrowserCommanderE2EEnvironment.IsEnabled && BrowserCommanderE2EEnvironment.IsHeadless)
        {
            Skip = "This end-to-end scenario requires a visible browser window.";
        }
    }
}
