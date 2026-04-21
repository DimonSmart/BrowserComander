using BrowserCommander.Contracts;

namespace BrowserCommanderServer;

public interface IBrowserAutomationService
{
    void RegisterAgent(string connectionId, BrowserAgentRegistration registration);

    void UpdateTabs(string connectionId, BrowserAgentTabsUpdate update);

    void CompleteCommand(string connectionId, BrowserAutomationResult result);

    void RemoveConnection(string connectionId);

    IReadOnlyCollection<BrowserAgentStatus> GetAgents();

    IReadOnlyCollection<BrowserPageSummary> GetPages();

    Task AuthorizeTabAsync(string agentId, int tabId);

    Task RevokeTabAsync(string agentId, int tabId);

    Task ClearAllAuthorizationsAsync();

    IReadOnlyCollection<int> GetAuthorizedTabIds(string agentId);

    Task<BrowserAutomationResult> ExecuteCommandAsync(BrowserAutomationCommand command, CancellationToken cancellationToken);
}
