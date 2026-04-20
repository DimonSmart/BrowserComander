namespace BrowserCommander;

public sealed class ExtensionSettings
{
    public string ServerAddress { get; set; } = string.Empty;

    public string DefaultServerAddress { get; set; } = string.Empty;

    public int CommandTimeoutMs { get; set; }

    public int DefaultCommandTimeoutMs { get; set; }
}
