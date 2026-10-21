using System.Text.Json;
using BrowserCommander.Contracts;
using Microsoft.Playwright;

namespace BrowserCommander.E2E.Tests.Infrastructure;

internal sealed class BrowserExtensionHarness
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private BrowserExtensionHarness(
        string userDataDirectoryPath,
        IBrowserContext context,
        IPage extensionPage,
        string extensionId)
    {
        UserDataDirectoryPath = userDataDirectoryPath;
        Context = context;
        ExtensionPage = extensionPage;
        ExtensionId = extensionId;
    }

    public string UserDataDirectoryPath { get; }

    public IBrowserContext Context { get; }

    public IPage ExtensionPage { get; }

    public string ExtensionId { get; }

    public static async Task<BrowserExtensionHarness> LaunchAsync(
        IPlaywright playwright,
        Uri serverBaseUri,
        CancellationToken cancellationToken = default)
    {
        var userDataDirectoryPath = Path.Combine(
            Path.GetTempPath(),
            $"browsercommander-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(userDataDirectoryPath);

        IBrowserContext? context = null;
        try
        {
            context = await playwright.Chromium.LaunchPersistentContextAsync(
                userDataDirectoryPath,
                new BrowserTypeLaunchPersistentContextOptions
                {
                    Channel = "chromium",
                    Headless = BrowserCommanderE2EEnvironment.IsHeadless,
                    Args =
                    [
                        $"--disable-extensions-except={BrowserCommanderE2EEnvironment.ExtensionPath}",
                        $"--load-extension={BrowserCommanderE2EEnvironment.ExtensionPath}"
                    ]
                });

            // Manifest V3 does not automatically open an extension page.
            var extensionId = await WaitForExtensionIdAsync(context, cancellationToken);
            var extensionPage = await context.NewPageAsync();
            await extensionPage.GotoAsync($"chrome-extension://{extensionId}/index.html");
            var harness = new BrowserExtensionHarness(userDataDirectoryPath, context, extensionPage, extensionId);
            var settings = await harness.SetServerAddressAsync(serverBaseUri);
            if (!settings.Ok)
            {
                throw new InvalidOperationException($"Extension settings failed: {settings.Error}");
            }
            return harness;
        }
        catch
        {
            if (context is not null)
            {
                await context.CloseAsync();
            }
            if (Directory.Exists(userDataDirectoryPath))
            {
                Directory.Delete(userDataDirectoryPath, recursive: true);
            }
            throw;
        }
    }

    public Task<BrowserExtensionStatus> SetServerAddressAsync(Uri serverBaseUri)
    {
        return SendMessageAsync<BrowserExtensionStatus>(new
        {
            type = "saveExtensionSettings",
            serverAddress = serverBaseUri.ToString(),
            commandTimeoutMs = BrowserCommandDefaults.TimeoutMs
        });
    }

    public Task<BrowserExtensionStatus> GetStatusAsync()
    {
        return SendMessageAsync<BrowserExtensionStatus>(new
        {
            type = "status"
        });
    }

    public async Task<BrowserExtensionStatus> AuthorizeTabAsync(IPage page)
    {
        return await SendTabMessageWithRetryAsync(page.Url, "authorizeTab");
    }

    public async Task<BrowserExtensionStatus> RevokeTabAsync(IPage page)
    {
        return await SendTabMessageWithRetryAsync(page.Url, "revokeTab");
    }

    public async Task DisposeAsync()
    {
        try
        {
            await Context.CloseAsync();
        }
        finally
        {
            if (Directory.Exists(UserDataDirectoryPath))
            {
                Directory.Delete(UserDataDirectoryPath, recursive: true);
            }
        }
    }

    private async Task<T> SendMessageAsync<T>(object payload)
    {
        var response = await ExtensionPage.EvaluateAsync<JsonElement>(
            "async message => await chrome.runtime.sendMessage(message)",
            payload);

        return Deserialize<T>(response);
    }

    private static async Task<string> WaitForExtensionIdAsync(
        IBrowserContext context, CancellationToken cancellationToken)
    {
        // Playwright .NET 1.58 does not expose BrowserContext.ServiceWorkers.
        // CDP Target.getTargets lists the same MV3 service_worker targets in Chromium.
        var discoveryPage = await context.NewPageAsync();
        var cdp = await context.NewCDPSessionAsync(discoveryPage);
        try
        {
            var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var targets = await cdp.SendAsync("Target.getTargets");
                if (targets is { } result && result.TryGetProperty("targetInfos", out var infos))
                {
                    foreach (var target in infos.EnumerateArray())
                    {
                        if (target.GetProperty("type").GetString() != "service_worker")
                        {
                            continue;
                        }
                        var url = target.GetProperty("url").GetString();
                        if (url?.StartsWith("chrome-extension://", StringComparison.Ordinal) == true)
                        {
                            return new Uri(url).Host;
                        }
                    }
                }
                await Task.Delay(200, cancellationToken);
            } while (DateTimeOffset.UtcNow < timeoutAtUtc);

            throw new TimeoutException(
                "MV3 service worker was not found in Chromium CDP targets within 30 seconds. " +
                $"Extension: '{BrowserCommanderE2EEnvironment.ExtensionPath}'.");
        }
        finally
        {
            await cdp.DetachAsync();
            await discoveryPage.CloseAsync();
        }
    }

    public async Task<int> GetTabIdAsync(IPage page)
    {
        var result = await ExtensionPage.EvaluateAsync<int?>(
            "async url => { const tabs = await chrome.tabs.query({}); return tabs.find(tab => tab.url === url)?.id ?? null; }", page.Url);
        return result ?? throw new InvalidOperationException($"Target tab not found: {page.Url}");
    }

    // Keep the HTTP tab active when Blazor loads popup.html in a separate extension tab.
    // The popup's Tabs.Query(active, currentWindow) must resolve the original user tab.
    public async Task ActivateTabAsync(IPage page)
    {
        var id = await GetTabIdAsync(page);
        await ExtensionPage.EvaluateAsync(
            "async id => { await chrome.tabs.update(id, { active: true }); }", id);
        var activeId = await ExtensionPage.EvaluateAsync<int?>(
            "async () => (await chrome.tabs.query({ active: true, currentWindow: true }))[0]?.id ?? null");
        if (activeId != id)
        {
            throw new InvalidOperationException($"Expected active tab #{id}, got #{activeId}.");
        }
    }

    private static T Deserialize<T>(JsonElement element)
    {
        return element.Deserialize<T>(JsonOptions) ?? throw new InvalidOperationException(
            $"Could not deserialize extension response to {typeof(T).Name}.");
    }

    private async Task<BrowserExtensionStatus> SendTabMessageWithRetryAsync(string targetUrl, string messageType)
    {
        BrowserExtensionStatus? lastStatus = null;
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            var response = await ExtensionPage.EvaluateAsync<JsonElement>(
                """
                async payload => {
                  const targetUrl = payload.targetUrl;
                  const origin = new URL(targetUrl).origin;
                  const tabs = await chrome.tabs.query({});
                  const candidates = tabs.filter(tab =>
                    typeof tab.url === 'string'
                    && (tab.url === targetUrl || tab.url.startsWith(targetUrl) || tab.url.startsWith(origin)));
                  const targetTab = candidates.find(tab => tab.active) ?? candidates[0];

                  if (!targetTab?.id) {
                    return {
                      ok: false,
                      error: `Tab '${targetUrl}' was not found in chrome.tabs.query(). Known tabs: ${tabs.map(tab => tab.url ?? '<null>').join(', ')}`
                    };
                  }

                  return await chrome.runtime.sendMessage({
                    type: payload.messageType,
                    tabId: targetTab.id
                  });
                }
                """,
                new
                {
                    targetUrl,
                    messageType
                });

            lastStatus = Deserialize<BrowserExtensionStatus>(response);
            if (lastStatus.Ok)
            {
                return lastStatus;
            }

            await Task.Delay(250);
        }

        return lastStatus ?? new BrowserExtensionStatus
        {
            Ok = false,
            Error = $"Timed out waiting to send '{messageType}' for tab '{targetUrl}'."
        };
    }
}

internal sealed class BrowserExtensionStatus
{
    public bool Ok { get; set; }

    public string? AgentId { get; set; }

    public bool Connected { get; set; }

    public string? Error { get; set; }
}
