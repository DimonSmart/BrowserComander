using Microsoft.JSInterop;

namespace BrowserCommander;

public sealed class JSInteropService
{
    private readonly IJSRuntime _jsRuntime;

    public JSInteropService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public ValueTask SetTextAsync(string selector, string text)
    {
        return _jsRuntime.InvokeVoidAsync("setTextFunctionScript", selector, text);
    }

    public ValueTask<string?> GetTextAsync(string selector)
    {
        return _jsRuntime.InvokeAsync<string?>("getTextFunctionScript", selector);
    }

    public ValueTask<BackgroundAgentStatus> GetBackgroundAgentStatusAsync()
    {
        return _jsRuntime.InvokeAsync<BackgroundAgentStatus>("getBackgroundAgentStatus");
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
