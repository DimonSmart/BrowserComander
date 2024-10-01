using Microsoft.AspNetCore.SignalR;

namespace BrowserCommanderServer
{
    public class BrowserCommanderHub : Hub
    {
        private readonly ILogger<BrowserCommanderHub> _logger;
        private readonly ITextStore _textStore;

        public BrowserCommanderHub(ILogger<BrowserCommanderHub> logger, ITextStore textStore)
        {
            _logger = logger;
            _textStore = textStore;
        }

        public async Task SetText(string setSelector, string text)
        {
            _textStore.Texts[setSelector] = text;

            _logger.LogInformation("SetText called with setSelector: {setSelector}, text: {text}", setSelector, text);

            await Clients.Others.SendAsync("TextSet", setSelector, text);
        }

        public async Task GetText(string getLocator)
        {
            if (_textStore.Texts.TryGetValue(getLocator, out var text))
            {
                await Clients.Caller.SendAsync("ReceiveText", text);
            }
            else
            {
                await Clients.Caller.SendAsync("ReceiveText", null);
            }

            _logger.LogInformation("GetText called with getLocator: {getLocator}", getLocator);
        }
    }
}
