using BrowserCommander.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class BrowserAutomationServiceTests
{
    [Fact]
    public void Pages_AreDistinctAcrossIndependentAgents_WithIdenticalTabIds()
    {
        var service = CreateService();
        service.RegisterAgent("edge", CreateRegistration("edge-agent", "Edge", CreateTab(7, "https://edge.example")));
        service.RegisterAgent("chrome", CreateRegistration("chrome-agent", "Chrome", CreateTab(7, "https://chrome.example")));
        Assert.Equal(2, service.GetPages().Count);
        Assert.Contains(service.GetPages(), p => p.PageId == BrowserPageRef.CreatePageId("edge-agent", 7));
        Assert.Contains(service.GetPages(), p => p.PageId == BrowserPageRef.CreatePageId("chrome-agent", 7));
    }

    [Fact]
    public void Registration_Takeover_PreservesPageId_AndOldDisconnectDoesNotDelete()
    {
        var service = CreateService();
        var registration = CreateRegistration("same-agent", "Chrome", CreateTab(7, "https://example.com"));
        var first = service.RegisterAgent("old", registration);
        var before = Assert.Single(service.GetPages()).PageId;
        var second = service.RegisterAgent("new", registration);
        service.RemoveConnection("old");
        Assert.Equal(before, Assert.Single(service.GetPages()).PageId);
        Assert.True(second.StateRevision == first.StateRevision);
        Assert.Equal("new", Assert.Single(service.GetAgents()).ConnectionId);
        Assert.Throws<InvalidOperationException>(() => service.UpdateTabs("old",
            new BrowserAgentTabsUpdate { AgentId = "same-agent", StateRevision = 2, Tabs = [] }));
    }

    [Fact]
    public void RevisionUpdate_IsAtomic_Idempotent_AndRejectsConflicts()
    {
        var service = CreateService();
        service.RegisterAgent("c1", CreateRegistration("agent", "Chrome", CreateTab(7, "https://old")));
        var next = new BrowserAgentTabsUpdate
        {
            AgentId = "agent", StateRevision = 2, Tabs = [CreateTab(8, "https://new")]
        };
        Assert.Equal(2, service.UpdateTabs("c1", next).StateRevision);
        Assert.Equal(2, service.UpdateTabs("c1", next).StateRevision);
        Assert.Throws<InvalidOperationException>(() => service.UpdateTabs("c1",
            new BrowserAgentTabsUpdate { AgentId = "agent", StateRevision = 1, Tabs = [] }));
        Assert.Throws<InvalidOperationException>(() => service.UpdateTabs("c1",
            new BrowserAgentTabsUpdate { AgentId = "agent", StateRevision = 2, Tabs = [] }));
        Assert.Equal(8, Assert.Single(service.GetPages()).TabId);
    }

    [Fact]
    public void Registration_RejectsOldProtocol_AndEnforces64TabsPerAgent()
    {
        var service = CreateService();
        var invalid = CreateRegistration("a", "Chrome", CreateTab(1, "https://example.com"));
        invalid.ProtocolVersion = "2";
        Assert.Throws<InvalidOperationException>(() => service.RegisterAgent("c", invalid));
        Assert.Empty(service.GetAgents());

        var accepted = CreateRegistration("a", "Chrome", CreateTab(1, "https://example.com"));
        accepted.Tabs = Enumerable.Range(1, 64).Select(n => CreateTab(n, "https://example.com")).ToList();
        service.RegisterAgent("c", accepted);
        var tooMany = new BrowserAgentTabsUpdate
        {
            AgentId = "a", StateRevision = 2,
            Tabs = Enumerable.Range(1, 65).Select(n => CreateTab(n, "https://example.com")).ToList()
        };
        Assert.Throws<InvalidOperationException>(() => service.UpdateTabs("c", tooMany));
        Assert.Equal(64, service.GetPages().Count);
        service.RegisterAgent("other", CreateRegistration("b", "Edge", CreateTab(1, "https://edge.example")));
        Assert.Equal(65, service.GetPages().Count);
    }

    [Fact]
    public async Task Command_RejectsUnauthorizedTab_AndDisconnectCompletesPending()
    {
        var proxy = new TestClientProxy();
        var service = CreateService(proxy: proxy);
        service.RegisterAgent("c1", CreateRegistration("a", "Chrome", CreateTab(7, "https://example.com")));
        var denied = await service.ExecuteCommandAsync(new BrowserAutomationCommand
        {
            AgentId = "a", TabId = 8, Action = BrowserCommandActions.PageTitle
        }, CancellationToken.None);
        Assert.Equal(BrowserCommandErrorCodes.TabNotAuthorized, denied.ErrorCode);

        var pending = service.ExecuteCommandAsync(new BrowserAutomationCommand
        {
            AgentId = "a", TabId = 7, Action = BrowserCommandActions.PageTitle
        }, CancellationToken.None);
        await proxy.WaitForCommandSentAsync();
        service.RemoveConnection("c1");
        Assert.Equal(BrowserCommandErrorCodes.AgentDisconnected, (await pending).ErrorCode);
    }

    [Fact]
    public async Task Completion_MustMatchConnectionAgentTabAndAction()
    {
        var proxy = new TestClientProxy();
        var service = CreateService(proxy: proxy);
        service.RegisterAgent("correct", CreateRegistration("a", "Chrome", CreateTab(7, "https://example.com")));
        var pending = service.ExecuteCommandAsync(new BrowserAutomationCommand
        {
            AgentId = "a", TabId = 7, Action = BrowserCommandActions.PageTitle
        }, CancellationToken.None);
        var dispatched = await proxy.WaitForCommandSentAsync();
        var response = new BrowserAutomationResult
        {
            CommandId = dispatched.CommandId, AgentId = "a", TabId = 7,
            Action = BrowserCommandActions.PageTitle, Success = true, Title = "OK"
        };
        Assert.Throws<InvalidOperationException>(() => service.CompleteCommand("other", response));
        response.TabId = 8;
        Assert.Throws<InvalidOperationException>(() => service.CompleteCommand("correct", response));
        response.TabId = 7;
        service.CompleteCommand("correct", response);
        Assert.Equal("OK", (await pending).Title);
    }

    [Fact]
    public async Task Takeover_CancelsCommandsFromDisplacedConnection()
    {
        var proxy = new TestClientProxy();
        var service = CreateService(proxy: proxy);
        var registration = CreateRegistration("a", "Chrome", CreateTab(7, "https://example.com"));
        service.RegisterAgent("old", registration);
        var pending = service.ExecuteCommandAsync(new BrowserAutomationCommand
        {
            AgentId = "a", TabId = 7, Action = BrowserCommandActions.PageTitle
        }, CancellationToken.None);
        await proxy.WaitForCommandSentAsync();
        service.RegisterAgent("new", registration);
        Assert.Equal("session_replaced", (await pending).ErrorCode);
        Assert.Single(service.GetPages());
    }

    [Fact]
    public async Task RevocationSnapshot_CancelsInFlightCommand()
    {
        var proxy = new TestClientProxy();
        var service = CreateService(proxy: proxy);
        service.RegisterAgent("c", CreateRegistration("a", "Chrome", CreateTab(7, "https://example.com")));
        var pending = service.ExecuteCommandAsync(new BrowserAutomationCommand
        {
            AgentId = "a", TabId = 7, Action = BrowserCommandActions.PageTitle
        }, CancellationToken.None);
        await proxy.WaitForCommandSentAsync();
        service.UpdateTabs("c", new BrowserAgentTabsUpdate { AgentId = "a", StateRevision = 2, Tabs = [] });
        Assert.Equal(BrowserCommandErrorCodes.TabNotAuthorized, (await pending).ErrorCode);
        Assert.Empty(service.GetPages());
    }

    [Fact]
    public void PageId_Encoding_RemainsBackwardsCompatible()
    {
        var pageId = BrowserPageRef.CreatePageId("shared:agent", 7);
        Assert.True(BrowserPageRef.TryParse(pageId, out var parsed));
        Assert.Equal("shared:agent", parsed.AgentId);
        Assert.Equal(7, parsed.TabId);
    }

    private static BrowserAutomationService CreateService(
        TestClientProxy? proxy = null,
        ILogger<BrowserAutomationService>? logger = null)
    {
        proxy ??= new TestClientProxy();
        logger ??= new TestLogger<BrowserAutomationService>();
        return new BrowserAutomationService(new TestHubContext(proxy), logger);
    }

    private static BrowserAgentRegistration CreateRegistration(string agentId, string browserName, BrowserTabDescriptor tab)
    {
        return new BrowserAgentRegistration
        {
            AgentId = agentId,
            BrowserName = browserName,
            ProtocolVersion = BrowserCommanderProtocol.Version,
            StateRevision = 1,
            Tabs = [tab]
        };
    }

    private static BrowserTabDescriptor CreateTab(int tabId, string url)
    {
        return new BrowserTabDescriptor
        {
            TabId = tabId,
            WindowId = 1,
            Active = true,
            Title = url,
            Url = url
        };
    }

    private sealed class TestHubContext : IHubContext<BrowserCommanderHub>
    {
        public TestHubContext(TestClientProxy proxy)
        {
            Clients = new TestHubClients(proxy);
        }

        public IHubClients Clients { get; }

        public IGroupManager Groups { get; } = new TestGroupManager();
    }

    private sealed class TestHubClients : IHubClients
    {
        private readonly IClientProxy _proxy;

        public TestHubClients(TestClientProxy proxy)
        {
            _proxy = proxy;
        }

        public IClientProxy All => _proxy;

        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Client(string connectionId) => _proxy;

        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;

        public IClientProxy Group(string groupName) => _proxy;

        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;

        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;

        public IClientProxy User(string userId) => _proxy;

        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class TestClientProxy : IClientProxy
    {
        private readonly TaskCompletionSource<BrowserAutomationCommand> _commandSent =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _sentMethods = [];

        public IReadOnlyList<string> SentMethods => _sentMethods;

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            _sentMethods.Add(method);

            if (string.Equals(method, BrowserCommanderHubMethods.ExecuteCommand, StringComparison.Ordinal)
                && args.FirstOrDefault() is BrowserAutomationCommand command)
            {
                _commandSent.TrySetResult(command);
            }

            return Task.CompletedTask;
        }

        public Task<BrowserAutomationCommand> WaitForCommandSentAsync()
        {
            return _commandSent.Task;
        }
    }

    private sealed class TestGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel LogLevel, string Message);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
