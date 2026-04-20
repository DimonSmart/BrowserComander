using Microsoft.AspNetCore.Hosting;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace BrowserCommanderServer;

internal static class ServerAddressAvailabilityProbe
{
    private static readonly TimeSpan IdentityRequestTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ServerAddressConflict?> DetectConflictAsync(
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        foreach (var configuredAddress in GetConfiguredAddresses(configuration))
        {
            if (!Uri.TryCreate(configuredAddress, UriKind.Absolute, out var uri))
            {
                continue;
            }

            foreach (var endpoint in GetProbeEndpoints(uri))
            {
                if (!CanBind(endpoint))
                {
                    var kind = await DetectConflictKindAsync(uri, cancellationToken);
                    return new ServerAddressConflict(configuredAddress, kind);
                }
            }
        }

        return null;
    }

    internal static string[] GetConfiguredAddresses(IConfiguration configuration)
    {
        var urls =
            configuration[WebHostDefaults.ServerUrlsKey]
            ?? configuration["Urls"]
            ?? configuration["ASPNETCORE_URLS"]
            ?? configuration["DOTNET_URLS"];

        return string.IsNullOrWhiteSpace(urls)
            ? []
            : urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static IEnumerable<IPEndPoint> GetProbeEndpoints(Uri uri)
    {
        if (uri.Port <= 0 || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            yield break;
        }

        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            yield return new IPEndPoint(IPAddress.Loopback, uri.Port);
            yield return new IPEndPoint(IPAddress.IPv6Loopback, uri.Port);
            yield break;
        }

        if (IPAddress.TryParse(uri.Host, out var ipAddress))
        {
            yield return new IPEndPoint(ipAddress, uri.Port);
            yield break;
        }

        if (uri.Host is "*" or "+" or "0.0.0.0")
        {
            yield return new IPEndPoint(IPAddress.Any, uri.Port);
            yield break;
        }

        if (uri.Host is "[::]" or "::")
        {
            yield return new IPEndPoint(IPAddress.IPv6Any, uri.Port);
        }
    }

    private static bool CanBind(IPEndPoint endpoint)
    {
        try
        {
            using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);

            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = false;
            }

            socket.Bind(endpoint);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<ServerAddressConflictKind> DetectConflictKindAsync(Uri uri, CancellationToken cancellationToken)
    {
        var identityUri = BuildDiagnosticUri(uri, "/whoami");

        if (await IsBrowserCommanderServerAsync(identityUri, cancellationToken))
        {
            return ServerAddressConflictKind.BrowserCommanderServerAlreadyRunning;
        }

        var healthUri = BuildDiagnosticUri(uri, "/health");

        return await IsBrowserCommanderServerAsync(healthUri, cancellationToken)
            ? ServerAddressConflictKind.BrowserCommanderServerAlreadyRunning
            : ServerAddressConflictKind.OtherServiceListening;
    }

    private static async Task<bool> IsBrowserCommanderServerAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var httpClient = new HttpClient(handler)
        {
            Timeout = IdentityRequestTimeout
        };
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(IdentityRequestTimeout);

        try
        {
            using var response = await httpClient.GetAsync(requestUri, timeoutCancellation.Token);

            if (response.Headers.TryGetValues(ServerIdentity.ResponseHeaderName, out var headerValues)
                && headerValues.Contains(ServerIdentity.ServiceName, StringComparer.Ordinal))
            {
                return true;
            }

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCancellation.Token);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeoutCancellation.Token);
            return document.RootElement.TryGetProperty("service", out var serviceName)
                && string.Equals(serviceName.GetString(), ServerIdentity.ServiceName, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Uri BuildDiagnosticUri(Uri uri, string path)
    {
        var builder = new UriBuilder(uri)
        {
            Host = uri.Host switch
            {
                "*" or "+" or "0.0.0.0" => IPAddress.Loopback.ToString(),
                "[::]" or "::" => IPAddress.IPv6Loopback.ToString(),
                _ => uri.Host
            },
            Path = path.TrimStart('/'),
            Query = string.Empty
        };

        return builder.Uri;
    }
}
