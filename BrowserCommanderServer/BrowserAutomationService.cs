using System.Collections.Concurrent;
using BrowserCommander.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace BrowserCommanderServer;

public sealed class BrowserAutomationService : IBrowserAutomationService
{
    private static readonly TimeSpan CompletedCommandRetention = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, BrowserAgentSession> _agentsById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _agentIdsByConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyCompletedCommands = new(StringComparer.Ordinal);
    private readonly IHubContext<BrowserCommanderHub> _hubContext;
    private readonly ILogger<BrowserAutomationService> _logger;

    public BrowserAutomationService(
        IHubContext<BrowserCommanderHub> hubContext,
        ILogger<BrowserAutomationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    public void RegisterAgent(string connectionId, BrowserAgentRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(registration.AgentId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        var session = _agentsById.AddOrUpdate(
            registration.AgentId,
            _ => BrowserAgentSession.FromRegistration(connectionId, registration, now),
            (_, existing) =>
            {
                existing.ConnectionId = connectionId;
                existing.ExtensionId = registration.ExtensionId;
                existing.BrowserName = registration.BrowserName;
                existing.UserAgent = registration.UserAgent;
                existing.ProtocolVersion = registration.ProtocolVersion;
                existing.Capabilities = CloneCapabilities(registration.Capabilities);
                existing.LastSeenAtUtc = now;
                existing.Tabs = CloneTabs(registration.Tabs);
                return existing;
            });

        _agentIdsByConnection[connectionId] = registration.AgentId;

        _logger.LogInformation(
            "Registered browser agent {AgentId} on connection {ConnectionId} with {TabCount} tab(s).",
            session.AgentId,
            session.ConnectionId,
            session.Tabs.Count);
    }

    public void UpdateTabs(string connectionId, BrowserAgentTabsUpdate update)
    {
        var agentId = ResolveAgentId(connectionId, update.AgentId);
        if (agentId is null || !_agentsById.TryGetValue(agentId, out var session))
        {
            _logger.LogWarning("Ignored tabs update from unknown connection {ConnectionId}.", connectionId);
            return;
        }

        session.LastSeenAtUtc = DateTimeOffset.UtcNow;
        session.Tabs = CloneTabs(update.Tabs);
    }

    public void CompleteCommand(string connectionId, BrowserAutomationResult result)
    {
        if (string.IsNullOrWhiteSpace(result.CommandId))
        {
            return;
        }

        var agentId = ResolveAgentId(connectionId, result.AgentId);
        if (agentId is not null && _agentsById.TryGetValue(agentId, out var session))
        {
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
        }

        if (_pendingCommands.TryGetValue(result.CommandId, out var pendingCommand))
        {
            pendingCommand.Completion.TrySetResult(result);
            return;
        }

        CleanupCompletedCommands();
        if (_recentlyCompletedCommands.TryRemove(result.CommandId, out _))
        {
            _logger.LogDebug(
                "Ignored late result for already-completed command {CommandId} from connection {ConnectionId}.",
                result.CommandId,
                connectionId);
            return;
        }

        _logger.LogWarning(
            "Received result for unknown command {CommandId} from connection {ConnectionId}.",
            result.CommandId,
            connectionId);
    }

    public void RemoveConnection(string connectionId)
    {
        if (!_agentIdsByConnection.TryRemove(connectionId, out var agentId))
        {
            return;
        }

        if (_agentsById.TryGetValue(agentId, out var session) && session.ConnectionId == connectionId)
        {
            _agentsById.TryRemove(agentId, out _);
        }

        foreach (var pendingPair in _pendingCommands.Values.Where(command => command.ConnectionId == connectionId))
        {
            pendingPair.Completion.TrySetResult(CreateFailureResult(
                pendingPair.Command,
                BrowserCommandErrorCodes.AgentDisconnected,
                "The browser agent disconnected before the command completed."));
        }

        _logger.LogInformation(
            "Removed browser agent {AgentId} for connection {ConnectionId}.",
            agentId,
            connectionId);
    }

    public IReadOnlyCollection<BrowserAgentStatus> GetAgents()
    {
        return _agentsById.Values
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
            .Select(agent => new BrowserAgentStatus
            {
                AgentId = agent.AgentId,
                ConnectionId = agent.ConnectionId,
                ExtensionId = agent.ExtensionId,
                BrowserName = agent.BrowserName,
                UserAgent = agent.UserAgent,
                ProtocolVersion = agent.ProtocolVersion,
                Capabilities = CloneCapabilities(agent.Capabilities),
                ConnectedAtUtc = agent.ConnectedAtUtc,
                LastSeenAtUtc = agent.LastSeenAtUtc,
                Tabs = CloneTabs(agent.Tabs)
            })
            .ToArray();
    }

    public IReadOnlyCollection<BrowserPageSummary> GetPages()
    {
        return _agentsById.Values
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
            .SelectMany(agent => agent.Tabs.Select(tab => new BrowserPageSummary
            {
                PageId = BrowserPageRef.CreatePageId(agent.AgentId, tab.TabId),
                AgentId = agent.AgentId,
                TabId = tab.TabId,
                WindowId = tab.WindowId,
                Active = tab.Active,
                Title = tab.Title,
                Url = tab.Url
            }))
            .OrderBy(page => page.AgentId, StringComparer.Ordinal)
            .ThenBy(page => page.TabId)
            .ToArray();
    }

    public async Task<BrowserAutomationResult> ExecuteCommandAsync(BrowserAutomationCommand command, CancellationToken cancellationToken)
    {
        if (!_agentsById.TryGetValue(command.AgentId, out var agent))
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.AgentNotFound,
                $"Browser agent '{command.AgentId}' is not connected.");
        }

        if (string.Equals(command.Action, BrowserCommandActions.ExecutePlan, StringComparison.Ordinal)
            && !agent.Capabilities.SupportsPlanExecution)
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.UnsupportedAction,
                $"Browser agent '{command.AgentId}' does not support server-driven execution plans.");
        }

        if (command.TimeoutMs <= 0)
        {
            command.TimeoutMs = 10000;
        }

        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            command.CommandId = Guid.NewGuid().ToString("N");
        }

        var pendingCommand = new PendingCommand(agent.ConnectionId, command);
        if (!_pendingCommands.TryAdd(command.CommandId, pendingCommand))
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.ExecutionFailed,
                $"Command '{command.CommandId}' is already pending.");
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(command.TimeoutMs);

            await _hubContext.Clients.Client(agent.ConnectionId)
                .SendAsync(BrowserCommanderHubMethods.ExecuteCommand, command, cancellationToken);

            return await pendingCommand.Completion.Task.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.Timeout,
                $"Timed out after {command.TimeoutMs} ms.");
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to execute command {CommandId} on agent {AgentId}.",
                command.CommandId,
                command.AgentId);

            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.ExecutionFailed,
                exception.Message);
        }
        finally
        {
            _pendingCommands.TryRemove(command.CommandId, out _);
            _recentlyCompletedCommands[command.CommandId] = DateTimeOffset.UtcNow;
            CleanupCompletedCommands();
        }
    }

    private string? ResolveAgentId(string connectionId, string? declaredAgentId)
    {
        if (!string.IsNullOrWhiteSpace(declaredAgentId))
        {
            return declaredAgentId;
        }

        return _agentIdsByConnection.TryGetValue(connectionId, out var agentId)
            ? agentId
            : null;
    }

    private static BrowserAutomationResult CreateFailureResult(
        BrowserAutomationCommand command,
        string errorCode,
        string error)
    {
        return new BrowserAutomationResult
        {
            CommandId = command.CommandId,
            AgentId = command.AgentId,
            TabId = command.TabId,
            Action = command.Action,
            Success = false,
            Url = command.Url,
            ErrorCode = errorCode,
            Error = error
        };
    }

    private static List<BrowserTabDescriptor> CloneTabs(IEnumerable<BrowserTabDescriptor>? tabs)
    {
        if (tabs is null)
        {
            return [];
        }

        return tabs.Select(tab => new BrowserTabDescriptor
        {
            TabId = tab.TabId,
            WindowId = tab.WindowId,
            Active = tab.Active,
            Url = tab.Url,
            Title = tab.Title
        }).ToList();
    }

    private static BrowserAgentCapabilities CloneCapabilities(BrowserAgentCapabilities? capabilities)
    {
        return new BrowserAgentCapabilities
        {
            SupportsPlanExecution = capabilities?.SupportsPlanExecution ?? false,
            SupportsContentScriptSteps = capabilities?.SupportsContentScriptSteps ?? false,
            SupportsDebuggerSteps = capabilities?.SupportsDebuggerSteps ?? false,
            SupportsTabSteps = capabilities?.SupportsTabSteps ?? false
        };
    }

    private void CleanupCompletedCommands()
    {
        var cutoff = DateTimeOffset.UtcNow - CompletedCommandRetention;

        foreach (var entry in _recentlyCompletedCommands)
        {
            if (entry.Value < cutoff)
            {
                _recentlyCompletedCommands.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed class BrowserAgentSession
    {
        public string AgentId { get; private init; } = string.Empty;

        public string ConnectionId { get; set; } = string.Empty;

        public string? ExtensionId { get; set; }

        public string? BrowserName { get; set; }

        public string? UserAgent { get; set; }

        public string? ProtocolVersion { get; set; }

        public BrowserAgentCapabilities Capabilities { get; set; } = new();

        public DateTimeOffset ConnectedAtUtc { get; private init; }

        public DateTimeOffset LastSeenAtUtc { get; set; }

        public List<BrowserTabDescriptor> Tabs { get; set; } = [];

        public static BrowserAgentSession FromRegistration(
            string connectionId,
            BrowserAgentRegistration registration,
            DateTimeOffset now)
        {
            return new BrowserAgentSession
            {
                AgentId = registration.AgentId,
                ConnectionId = connectionId,
                ExtensionId = registration.ExtensionId,
                BrowserName = registration.BrowserName,
                UserAgent = registration.UserAgent,
                ProtocolVersion = registration.ProtocolVersion,
                Capabilities = CloneCapabilities(registration.Capabilities),
                ConnectedAtUtc = now,
                LastSeenAtUtc = now,
                Tabs = CloneTabs(registration.Tabs)
            };
        }
    }

    private sealed class PendingCommand
    {
        public PendingCommand(string connectionId, BrowserAutomationCommand command)
        {
            ConnectionId = connectionId;
            Command = new BrowserAutomationCommand
            {
                CommandId = command.CommandId,
                AgentId = command.AgentId,
                TabId = command.TabId,
                FrameId = command.FrameId,
                Action = command.Action,
                Plan = command.Plan,
                Selector = command.Selector,
                SourceSelector = command.SourceSelector,
                TargetSelector = command.TargetSelector,
                Text = command.Text,
                Key = command.Key,
                Button = command.Button,
                MoveSteps = command.MoveSteps,
                Url = command.Url,
                MatchMode = command.MatchMode,
                WaitState = command.WaitState,
                Script = command.Script,
                Query = command.Query,
                OnlyVisible = command.OnlyVisible,
                InteractiveOnly = command.InteractiveOnly,
                Format = command.Format,
                Limit = command.Limit,
                ClearBuffer = command.ClearBuffer,
                TimeoutMs = command.TimeoutMs
            };
        }

        public string ConnectionId { get; }

        public BrowserAutomationCommand Command { get; }

        public TaskCompletionSource<BrowserAutomationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
