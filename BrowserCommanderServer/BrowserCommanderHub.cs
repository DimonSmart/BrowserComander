using BrowserCommander.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace BrowserCommanderServer;

public class BrowserCommanderHub : Hub
{
    private readonly ILogger<BrowserCommanderHub> _logger;
    private readonly IBrowserAutomationService _browserAutomationService;

    public BrowserCommanderHub(
        ILogger<BrowserCommanderHub> logger,
        IBrowserAutomationService browserAutomationService)
    {
        _logger = logger;
        _browserAutomationService = browserAutomationService;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _browserAutomationService.RemoveConnection(Context.ConnectionId);

        if (exception != null)
        {
            _logger.LogWarning(exception, "Client disconnected due to an error: {ConnectionId}", Context.ConnectionId);
        }
        else
        {
            _logger.LogInformation("Client disconnected: {ConnectionId}", Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public Task RegisterAgent(BrowserAgentRegistration registration)
    {
        _browserAutomationService.RegisterAgent(Context.ConnectionId, registration);
        return Task.CompletedTask;
    }

    public Task UpdateTabs(BrowserAgentTabsUpdate update)
    {
        _browserAutomationService.UpdateTabs(Context.ConnectionId, update);
        return Task.CompletedTask;
    }

    public Task CompleteCommand(BrowserAutomationResult result)
    {
        _browserAutomationService.CompleteCommand(Context.ConnectionId, result);
        return Task.CompletedTask;
    }
}
