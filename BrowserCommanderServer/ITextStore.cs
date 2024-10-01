namespace BrowserCommanderServer
{
    public interface ITextStore
    {
        IDictionary<string, string> Texts { get; }
    }
}
