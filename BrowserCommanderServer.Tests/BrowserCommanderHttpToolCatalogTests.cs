using System.Text.Json;
using BrowserCommanderServer;
using ModelContextProtocol.Server;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class BrowserCommanderHttpToolCatalogTests
{
    [Fact]
    public void DefaultMode_PreservesToolReadOnlyHints()
    {
        var catalog = new BrowserCommanderHttpToolCatalog(new McpToolPresentationOptions());

        var pageUrl = GetTool(catalog, "page_url");
        var locatorFill = GetTool(catalog, "locator_fill");

        Assert.Equal(30, catalog.Tools.Count);
        Assert.True(pageUrl.ProtocolTool.Annotations?.ReadOnlyHint is true);
        Assert.True(locatorFill.ProtocolTool.Annotations?.ReadOnlyHint is false);
    }

    [Fact]
    public void ForcedMode_MarksWriteToolsAsReadOnlyWithoutChangingSchemas()
    {
        var defaultCatalog = new BrowserCommanderHttpToolCatalog(new McpToolPresentationOptions());
        var forcedCatalog = new BrowserCommanderHttpToolCatalog(new McpToolPresentationOptions
        {
            ForceReadOnlyHints = true
        });

        Assert.Equal(defaultCatalog.Tools.Count, forcedCatalog.Tools.Count);

        var defaultLocatorFill = GetTool(defaultCatalog, "locator_fill").ProtocolTool;
        var forcedLocatorFill = GetTool(forcedCatalog, "locator_fill").ProtocolTool;
        var forcedPageGoto = GetTool(forcedCatalog, "page_goto").ProtocolTool;
        var forcedPageEvaluate = GetTool(forcedCatalog, "page_evaluate").ProtocolTool;

        Assert.True(forcedLocatorFill.Annotations?.ReadOnlyHint is true);
        Assert.True(forcedLocatorFill.Annotations?.DestructiveHint is false);
        Assert.True(forcedPageGoto.Annotations?.ReadOnlyHint is true);
        Assert.True(forcedPageGoto.Annotations?.DestructiveHint is false);
        Assert.True(forcedPageEvaluate.Annotations?.ReadOnlyHint is true);
        Assert.True(forcedPageEvaluate.Annotations?.DestructiveHint is false);

        Assert.Equal(defaultLocatorFill.Name, forcedLocatorFill.Name);
        Assert.Equal(defaultLocatorFill.Title, forcedLocatorFill.Title);
        Assert.Equal(defaultLocatorFill.Description, forcedLocatorFill.Description);
        Assert.Equal(
            JsonSerializer.Serialize(defaultLocatorFill.InputSchema),
            JsonSerializer.Serialize(forcedLocatorFill.InputSchema));
    }

    private static McpServerTool GetTool(BrowserCommanderHttpToolCatalog catalog, string name)
    {
        return Assert.Single(catalog.Tools, tool => string.Equals(tool.ProtocolTool.Name, name, StringComparison.Ordinal));
    }
}
