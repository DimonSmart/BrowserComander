namespace BrowserCommanderServer;

internal static class ServerIdentity
{
    public static readonly string InstanceId = Guid.NewGuid().ToString("D");
    public const string ServiceName = "BrowserCommanderServer";
    public const string ResponseHeaderName = "X-BrowserCommander-Service";
}
