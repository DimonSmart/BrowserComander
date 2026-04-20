using Microsoft.JSInterop;

namespace BrowserCommander;

public sealed class JSInteropService
{
    private readonly IJSRuntime _jsRuntime;

    public JSInteropService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public ValueTask<BackgroundAgentStatus> GetBackgroundAgentStatusAsync()
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>("getBackgroundAgentStatus");
    }

    public ValueTask<ExtensionSettings> GetExtensionSettingsAsync()
    {
        return _jsRuntime.InvokeAsync<ExtensionSettings>("getExtensionSettings");
    }

    public ValueTask<BackgroundAgentStatus> SaveExtensionSettingsAsync(string serverAddress, int commandTimeoutMs)
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>(
            "saveExtensionSettings",
            serverAddress,
            commandTimeoutMs);
    }

    public ValueTask<BackgroundAgentStatus> AuthorizeTabAsync(int tabId)
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>("authorizeTab", tabId);
    }

    public ValueTask<BackgroundAgentStatus> RevokeTabAsync(int tabId)
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>("revokeTab", tabId);
    }

    public ValueTask<BackgroundAgentStatus> ClearAuthorizedTabsAsync()
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>("clearAuthorizedTabs");
    }

    public ValueTask<GlobalPagesResult> GetGlobalPagesAsync()
    {
        return _jsRuntime.InvokeAsync<GlobalPagesResult>("getGlobalPages");
    }
}
