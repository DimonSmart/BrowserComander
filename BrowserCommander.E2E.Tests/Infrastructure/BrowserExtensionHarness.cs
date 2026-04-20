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

        var context = await playwright.Chromium.LaunchPersistentContextAsync(
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

        var extensionPage = await WaitForExtensionPageAsync(context, cancellationToken);
        var extensionId = new Uri(extensionPage.Url).Host;

        var harness = new BrowserExtensionHarness(userDataDirectoryPath, context, extensionPage, extensionId);
        await harness.SetServerAddressAsync(serverBaseUri);
        return harness;
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
        await Context.CloseAsync();

        if (Directory.Exists(UserDataDirectoryPath))
        {
            Directory.Delete(UserDataDirectoryPath, recursive: true);
        }
    }

    private async Task<T> SendMessageAsync<T>(object payload)
    {
        var response = await ExtensionPage.EvaluateAsync<JsonElement>(
            "async message => await chrome.runtime.sendMessage(message)",
            payload);

        return Deserialize<T>(response);
    }

    private static async Task<IPage> WaitForExtensionPageAsync(IBrowserContext context, CancellationToken cancellationToken)
    {
        var timeoutAtUtc = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < timeoutAtUtc)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var extensionPage = context.Pages
                .FirstOrDefault(candidate => candidate.Url.StartsWith("chrome-extension://", StringComparison.Ordinal));

            if (extensionPage is not null)
            {
                return extensionPage;
            }

            await Task.Delay(200, cancellationToken);
        }

        throw new TimeoutException("Timed out waiting for the BrowserCommander extension page.");
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
