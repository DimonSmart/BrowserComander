namespace BrowserCommanderServer
{
    public class InMemoryTextStore : ITextStore
    {
        public IDictionary<string, string> Texts { get; } = new Dictionary<string, string>();
    }
}
