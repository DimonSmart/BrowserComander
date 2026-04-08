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

    public ValueTask<ServerAddressSettings> GetServerAddressSettingsAsync()
    {
        return _jsRuntime.InvokeAsync<ServerAddressSettings>("getServerAddressSettings");
    }

    public ValueTask<BackgroundAgentStatus> SetServerAddressAsync(string serverAddress)
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>("setServerAddress", serverAddress);
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
}
