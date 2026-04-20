namespace BrowserCommanderServer;

internal enum ServerAddressConflictKind
{
    BrowserCommanderServerAlreadyRunning,
    OtherServiceListening
}

internal sealed record ServerAddressConflict(string Address, ServerAddressConflictKind Kind);
