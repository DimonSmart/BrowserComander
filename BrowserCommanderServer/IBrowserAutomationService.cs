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

    void AuthorizeTab(string agentId, int tabId);

    void RevokeTab(string agentId, int tabId);

    void ClearAllAuthorizations();

    IReadOnlyCollection<int> GetAuthorizedTabIds(string agentId);

    Task<BrowserAutomationResult> ExecuteCommandAsync(BrowserAutomationCommand command, CancellationToken cancellationToken);
}
