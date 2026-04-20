using BrowserCommander.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class BrowserAutomationServiceTests
{
    [Fact]
    public void BrowserPageRef_RoundTripsAgentIdsThatRequireEscaping()
    {
        var pageId = BrowserPageRef.CreatePageId("shared:agent~edge-connection", 7);

        var parsed = BrowserPageRef.TryParse(pageId, out var pageRef);

        Assert.True(parsed);
        Assert.Equal("shared:agent~edge-connection", pageRef.AgentId);
        Assert.Equal(7, pageRef.TabId);
    }

    [Fact]
    public void GetPages_KeepsPagesDistinct_WhenTwoBrowsersUseSameTabId()
    {
        var service = CreateService();

        service.RegisterAgent("edge-connection", CreateRegistration("edge-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));
        service.RegisterAgent("chrome-connection", CreateRegistration("chrome-agent", "Google Chrome", CreateTab(7, "https://chrome.example")));
        service.AuthorizeTab("edge-agent", 7);
        service.AuthorizeTab("chrome-agent", 7);

        var pages = service.GetPages()
            .OrderBy(page => page.AgentId, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, pages.Length);
        Assert.Equal(["page:chrome-agent:7", "page:edge-agent:7"], pages.Select(page => page.PageId).ToArray());
        Assert.Equal(new string?[] { "Google Chrome", "Microsoft Edge" }, pages.Select(page => page.BrowserName).ToArray());
    }

    [Fact]
    public void RegisterAgent_AssignsUniqueServerSessionId_WhenReportedAgentIdCollides()
    {
        var service = CreateService();

        service.RegisterAgent("edge-connection", CreateRegistration("shared-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));
        service.RegisterAgent("chrome-connection", CreateRegistration("shared-agent", "Google Chrome", CreateTab(9, "https://chrome.example")));
        service.AuthorizeTab("shared-agent", 7);
        service.AuthorizeTab("shared-agent~chrome-connection", 9);

        var pages = service.GetPages()
            .OrderBy(page => page.BrowserName, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(2, pages.Length);

        var chromePage = Assert.Single(pages, page => page.BrowserName == "Google Chrome");
        var edgePage = Assert.Single(pages, page => page.BrowserName == "Microsoft Edge");

        Assert.Equal("shared-agent", edgePage.AgentId);
        Assert.StartsWith("shared-agent~chrome-connection", chromePage.AgentId, StringComparison.Ordinal);
        Assert.Equal("shared-agent", chromePage.ReportedAgentId);
        Assert.NotEqual(edgePage.PageId, chromePage.PageId);
    }

    [Fact]
    public void UpdateTabs_UsesConnectionBinding_WhenReportedAgentIdCollides()
    {
        var service = CreateService();

        service.RegisterAgent("edge-connection", CreateRegistration("shared-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));
        service.RegisterAgent("chrome-connection", CreateRegistration("shared-agent", "Google Chrome", CreateTab(9, "https://chrome.example")));
        service.AuthorizeTab("shared-agent", 7);
        service.AuthorizeTab("shared-agent~chrome-connection", 15);

        service.UpdateTabs("chrome-connection", new BrowserAgentTabsUpdate
        {
            AgentId = "shared-agent",
            Tabs =
            [
                CreateTab(15, "https://chrome-updated.example")
            ]
        });

        var pages = service.GetPages()
            .OrderBy(page => page.BrowserName, StringComparer.Ordinal)
            .ToArray();

        Assert.Collection(
            pages,
            page =>
            {
                Assert.Equal("Google Chrome", page.BrowserName);
                Assert.Equal(15, page.TabId);
                Assert.StartsWith("shared-agent~chrome-connection", page.AgentId, StringComparison.Ordinal);
            },
            page =>
            {
                Assert.Equal("Microsoft Edge", page.BrowserName);
                Assert.Equal(7, page.TabId);
                Assert.Equal("shared-agent", page.AgentId);
            });
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsTabNotAuthorized_WhenTabWasNotAuthorized()
    {
        var service = CreateService();
        service.RegisterAgent("edge-connection", CreateRegistration("edge-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));

        var result = await service.ExecuteCommandAsync(
            new BrowserAutomationCommand
            {
                AgentId = "edge-agent",
                TabId = 7,
                Action = BrowserCommandActions.PageTitle
            },
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(BrowserCommandErrorCodes.TabNotAuthorized, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsRequestAborted_WhenCallerCancelsRequest()
    {
        var proxy = new TestClientProxy();
        var service = CreateService(proxy: proxy);
        service.RegisterAgent("edge-connection", CreateRegistration("edge-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));
        service.AuthorizeTab("edge-agent", 7);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.ExecuteCommandAsync(
            new BrowserAutomationCommand
            {
                AgentId = "edge-agent",
                TabId = 7,
                Action = BrowserCommandActions.PageTitle
            },
            cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal(BrowserCommandErrorCodes.RequestAborted, result.ErrorCode);
    }

    [Fact]
    public async Task ExecuteCommand_ReturnsAgentDisconnected_WhenConnectionIsRemovedWhilePending()
    {
        var proxy = new TestClientProxy();
        var service = CreateService(proxy: proxy);
        service.RegisterAgent("edge-connection", CreateRegistration("edge-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));
        service.AuthorizeTab("edge-agent", 7);

        var executionTask = service.ExecuteCommandAsync(
            new BrowserAutomationCommand
            {
                AgentId = "edge-agent",
                TabId = 7,
                Action = BrowserCommandActions.PageTitle
            },
            CancellationToken.None);

        await proxy.WaitForCommandSentAsync();
        service.RemoveConnection("edge-connection");

        var result = await executionTask;

        Assert.False(result.Success);
        Assert.Equal(BrowserCommandErrorCodes.AgentDisconnected, result.ErrorCode);
    }

    [Fact]
    public async Task CompleteCommand_LogsLateResult_WhenResultArrivesAfterCallerCanceled()
    {
        var proxy = new TestClientProxy();
        var logger = new TestLogger<BrowserAutomationService>();
        var service = CreateService(proxy, logger);
        service.RegisterAgent("edge-connection", CreateRegistration("edge-agent", "Microsoft Edge", CreateTab(7, "https://edge.example")));
        service.AuthorizeTab("edge-agent", 7);

        using var cancellation = new CancellationTokenSource();
        var executionTask = service.ExecuteCommandAsync(
            new BrowserAutomationCommand
            {
                AgentId = "edge-agent",
                TabId = 7,
                Action = BrowserCommandActions.PageTitle
            },
            cancellation.Token);

        var sentCommand = await proxy.WaitForCommandSentAsync();
        cancellation.Cancel();

        var abortedResult = await executionTask;
        Assert.Equal(BrowserCommandErrorCodes.RequestAborted, abortedResult.ErrorCode);

        service.CompleteCommand("edge-connection", new BrowserAutomationResult
        {
            CommandId = sentCommand.CommandId,
            AgentId = "edge-agent",
            TabId = 7,
            Action = BrowserCommandActions.PageTitle,
            Success = true,
            Title = "Recovered title"
        });

        Assert.Contains(
            logger.Entries,
            entry => entry.LogLevel == LogLevel.Warning
                     && entry.Message.Contains("late result", StringComparison.OrdinalIgnoreCase)
                     && entry.Message.Contains(BrowserCommandErrorCodes.RequestAborted, StringComparison.OrdinalIgnoreCase));
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

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
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
