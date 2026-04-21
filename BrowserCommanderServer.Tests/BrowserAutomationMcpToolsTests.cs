using System.Text;
using BrowserCommander.Contracts;
using BrowserCommanderServer;
using ModelContextProtocol.Protocol;
using Xunit;

namespace BrowserCommanderServer.Tests;

public sealed class BrowserAutomationMcpToolsTests
{
    [Fact]
    public async Task PageEvaluate_UsesAgentDefaultTimeout_WhenTimeoutIsOmitted()
    {
        var service = new CapturingAutomationService
        {
            Agents =
            [
                new BrowserAgentStatus
                {
                    AgentId = "agent-1",
                    DefaultCommandTimeoutMs = 45000
                }
            ],
            Pages =
            [
                new BrowserPageSummary
                {
                    PageId = "page:agent-1:7",
                    AgentId = "agent-1",
                    ReportedAgentId = "agent-1",
                    TabId = 7,
                    Active = true,
                    Title = "Index",
                    Url = "https://example.test/"
                }
            ],
            NextResult = new BrowserAutomationResult
            {
                Success = true,
                ValueJson = "{\"width\":390}"
            }
        };

        var result = await BrowserAutomationMcpTools.PageEvaluate(
            pageId: "current",
            expression: "window.innerWidth",
            timeoutMs: 0,
            services: new TestServiceProvider(service));

        Assert.True(result.Success);
        Assert.NotNull(service.LastCommand);
        Assert.Equal("agent-1", service.LastCommand!.AgentId);
        Assert.Equal(7, service.LastCommand.TabId);
        Assert.Equal(BrowserCommandActions.ExecutePlan, service.LastCommand.Action);
        Assert.Equal(45000, service.LastCommand.TimeoutMs);

        var step = Assert.Single(service.LastCommand.Plan!.Steps);
        Assert.Equal(BrowserExecutionStepKinds.Debugger, step.Kind);
        Assert.Equal(BrowserExecutionOperations.Evaluate, step.Operation);
        Assert.Equal("window.innerWidth", step.Script);
    }

    [Fact]
    public async Task PageEvaluate_UsesExplicitTimeout_WhenProvided()
    {
        var service = new CapturingAutomationService
        {
            Agents =
            [
                new BrowserAgentStatus
                {
                    AgentId = "agent-1",
                    DefaultCommandTimeoutMs = 45000
                }
            ],
            Pages =
            [
                new BrowserPageSummary
                {
                    PageId = "page:agent-1:7",
                    AgentId = "agent-1",
                    ReportedAgentId = "agent-1",
                    TabId = 7,
                    Active = true
                }
            ]
        };

        await BrowserAutomationMcpTools.PageEvaluate(
            pageId: "current",
            expression: "window.innerWidth",
            timeoutMs: 1200,
            services: new TestServiceProvider(service));

        Assert.NotNull(service.LastCommand);
        Assert.Equal(1200, service.LastCommand!.TimeoutMs);
    }

    [Fact]
    public async Task PageEvaluate_FallsBackToPackagedDefaultTimeout_WhenAgentDefaultIsUnavailable()
    {
        var service = new CapturingAutomationService
        {
            Pages =
            [
                new BrowserPageSummary
                {
                    PageId = "page:agent-1:7",
                    AgentId = "agent-1",
                    ReportedAgentId = "agent-1",
                    TabId = 7,
                    Active = true
                }
            ]
        };

        await BrowserAutomationMcpTools.PageEvaluate(
            pageId: "current",
            expression: "window.innerWidth",
            timeoutMs: 0,
            services: new TestServiceProvider(service));

        Assert.NotNull(service.LastCommand);
        Assert.Equal(BrowserCommandDefaults.TimeoutMs, service.LastCommand!.TimeoutMs);
    }

    [Fact]
    public async Task PageFindLocators_NormalizesNonPositiveLimitToDefault()
    {
        var service = new CapturingAutomationService
        {
            Pages =
            [
                new BrowserPageSummary
                {
                    PageId = "page:agent-1:7",
                    AgentId = "agent-1",
                    ReportedAgentId = "agent-1",
                    TabId = 7,
                    Active = true
                }
            ],
            NextResult = new BrowserAutomationResult
            {
                Success = true,
                ValueJson = "[]"
            }
        };

        var result = await BrowserAutomationMcpTools.PageFindLocators(
            pageId: "current",
            query: "button 1",
            onlyVisible: true,
            interactiveOnly: false,
            limit: 0,
            timeoutMs: 0,
            services: new TestServiceProvider(service));

        Assert.True(result.Success);
        var step = Assert.Single(service.LastCommand!.Plan!.Steps);
        Assert.Equal(100, step.Limit);
        Assert.True(step.OnlyVisible);
        Assert.False(step.InteractiveOnly);
    }

    [Fact]
    public async Task PageTitle_ReturnsValidationFailure_WhenPageIdIsInvalid()
    {
        var result = await BrowserAutomationMcpTools.PageTitle(
            pageId: "not-a-page-id",
            services: new TestServiceProvider(new CapturingAutomationService()));

        Assert.False(result.Success);
        Assert.Equal(BrowserCommandErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Contains("Invalid pageId", result.Error);
    }

    [Fact]
    public async Task PageSetViewportPreset_ReturnsValidationFailure_WhenPresetIsUnknown()
    {
        var result = await BrowserAutomationMcpTools.PageSetViewportPreset(
            pageId: "page:agent-1:7",
            preset: "unknown-device",
            services: new TestServiceProvider(new CapturingAutomationService()));

        Assert.False(result.Success);
        Assert.Equal(BrowserCommandErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Contains("Unknown viewport preset", result.Error);
    }

    [Fact]
    public async Task PageScreenshot_ReturnsImageContentBlock_WhenDataIsValid()
    {
        var service = new CapturingAutomationService
        {
            NextResult = new BrowserAutomationResult
            {
                Success = true,
                ScreenshotBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake-image-bytes"))
            }
        };

        var result = await BrowserAutomationMcpTools.PageScreenshot(
            pageId: "page:agent-1:7",
            format: "png",
            services: new TestServiceProvider(service));

        Assert.False(result.IsError);
        var image = Assert.IsType<ImageContentBlock>(Assert.Single(result.Content!));
        Assert.Equal("image/png", image.MimeType);
        Assert.True(image.Data.Length > 0);
    }

    [Fact]
    public async Task PageScreenshot_ReturnsError_WhenDataIsInvalid()
    {
        var service = new CapturingAutomationService
        {
            NextResult = new BrowserAutomationResult
            {
                Success = true,
                ScreenshotBase64 = "!!!not-base64!!!"
            }
        };

        var result = await BrowserAutomationMcpTools.PageScreenshot(
            pageId: "page:agent-1:7",
            format: "png",
            services: new TestServiceProvider(service));

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content!));
        Assert.Contains("invalid image data", text.Text, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class TestServiceProvider : IServiceProvider
    {
        private readonly IBrowserAutomationService _service;

        public TestServiceProvider(IBrowserAutomationService service)
        {
            _service = service;
        }

        public object? GetService(Type serviceType)
        {
            return serviceType == typeof(IBrowserAutomationService)
                ? _service
                : null;
        }
    }

    private sealed class CapturingAutomationService : IBrowserAutomationService
    {
        public IReadOnlyCollection<BrowserAgentStatus> Agents { get; set; } = [];

        public IReadOnlyCollection<BrowserPageSummary> Pages { get; set; } = [];

        public BrowserAutomationResult NextResult { get; set; } = new()
        {
            Success = true
        };

        public BrowserAutomationCommand? LastCommand { get; private set; }

        public void RegisterAgent(string connectionId, BrowserAgentRegistration registration)
        {
        }

        public void UpdateTabs(string connectionId, BrowserAgentTabsUpdate update)
        {
        }

        public void CompleteCommand(string connectionId, BrowserAutomationResult result)
        {
        }

        public void RemoveConnection(string connectionId)
        {
        }

        public IReadOnlyCollection<BrowserAgentStatus> GetAgents() => Agents;

        public IReadOnlyCollection<BrowserPageSummary> GetPages() => Pages;

        public Task AuthorizeTabAsync(string agentId, int tabId)
        {
            return Task.CompletedTask;
        }

        public Task RevokeTabAsync(string agentId, int tabId)
        {
            return Task.CompletedTask;
        }

        public Task ClearAllAuthorizationsAsync()
        {
            return Task.CompletedTask;
        }

        public IReadOnlyCollection<int> GetAuthorizedTabIds(string agentId) => [];

        public Task<BrowserAutomationResult> ExecuteCommandAsync(
            BrowserAutomationCommand command,
            CancellationToken cancellationToken)
        {
            LastCommand = command;
            return Task.FromResult(NextResult);
        }
    }
}
