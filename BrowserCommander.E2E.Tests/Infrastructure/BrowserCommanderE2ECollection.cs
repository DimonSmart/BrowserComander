using Xunit;

namespace BrowserCommander.E2E.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class BrowserCommanderE2ECollection : ICollectionFixture<BrowserCommanderE2EFixture>
{
    public const string Name = "BrowserCommander E2E";
}
