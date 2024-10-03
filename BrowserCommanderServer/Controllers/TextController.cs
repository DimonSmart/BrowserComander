using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace BrowserCommanderServer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class TextController : ControllerBase
    {
        private readonly ILogger<TextController> _logger;
        private readonly ITextStore _textStore;
        private readonly IHubContext<BrowserCommanderHub> _hubContext;

        public TextController(
            ILogger<TextController> logger,
            ITextStore textStore,
            IHubContext<BrowserCommanderHub> hubContext)
        {
            _logger = logger;
            _textStore = textStore;
            _hubContext = hubContext;
        }

        [HttpGet("getText")]
        public IActionResult GetText([FromQuery] string getLocator)
        {
            if (string.IsNullOrEmpty(getLocator))
            {
                _logger.LogWarning("getText called without getLocator parameter.");
                return BadRequest(new { message = "getLocator parameter is required." });
            }

            _logger.LogInformation("Received getText call with getLocator: {getLocator}", getLocator);

            if (_textStore.Texts.TryGetValue(getLocator, out var text))
            {
                return Ok(new { text });
            }
            else
            {
                return NotFound(new { message = "Text not found for the given getLocator." });
            }
        }

        [HttpPost("setText")]
        public async Task<IActionResult> SetText([FromBody] SetTextRequest formData)
        {
            if (formData == null || string.IsNullOrEmpty(formData.SetSelector) || string.IsNullOrEmpty(formData.Text))
            {
                _logger.LogWarning("setText called with missing parameters.");
                return BadRequest(new { message = "setSelector and text parameters are required." });
            }

            _logger.LogInformation("Received setText call with setSelector: {setSelector}, text: {text}", formData.SetSelector, formData.Text);

            _textStore.Texts[formData.SetSelector] = formData.Text;

            if (_hubContext != null)
            {
                await _hubContext.Clients.All.SendAsync("TextUpdated", formData.SetSelector, formData.Text);
            }

            return Ok(new { message = "Text set successfully." });
        }
    }
}
