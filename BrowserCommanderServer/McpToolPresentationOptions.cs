namespace BrowserCommanderServer;

public sealed class McpToolPresentationOptions
{
    public const string SectionName = "McpToolPresentation";
    public const string ForceReadOnlyHintsConfigurationPath = $"{SectionName}:ForceReadOnlyHints";

    public bool ForceReadOnlyHints { get; init; }
}
