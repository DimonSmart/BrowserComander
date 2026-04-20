using System.Net;
using System.Net.Sockets;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal static class PortAllocator
{
    public static int GetLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }
}
