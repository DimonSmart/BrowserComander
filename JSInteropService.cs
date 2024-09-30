using Microsoft.JSInterop;
using System.Threading.Tasks;

namespace BrowserComander
{
    public class JSInteropService
    {
        private readonly IJSRuntime _jsRuntime;

        public JSInteropService(IJSRuntime jsRuntime)
        {
            _jsRuntime = jsRuntime;
        }

        public async Task SetTextAsync(string selector, string text)
        {
            await _jsRuntime.InvokeVoidAsync("setTextFunctionScript", selector, text);
        }

        public async Task<string> GetTextAsync(string selector)
        {
            return await _jsRuntime.InvokeAsync<string>("getTextFunctionScript", selector);
        }
    }
}