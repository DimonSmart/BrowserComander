using System.Collections.Concurrent;
using System.Diagnostics;
using BrowserCommander.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace BrowserCommanderServer;

public sealed class BrowserAutomationService : IBrowserAutomationService
{
    private static readonly TimeSpan CompletedCommandRetention = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<string, BrowserAgentSession> _agentsById = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _agentIdsByConnection = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingCommand> _pendingCommands = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CompletedCommandRecord> _recentlyCompletedCommands = new(StringComparer.Ordinal);
    private readonly object _sessionGate = new();
    private long _nextGeneration;
    private readonly IHubContext<BrowserCommanderHub> _hubContext;
    private readonly ILogger<BrowserAutomationService> _logger;

    public BrowserAutomationService(
        IHubContext<BrowserCommanderHub> hubContext,
        ILogger<BrowserAutomationService> logger)
    {
        _hubContext = hubContext;
        _logger = logger;
    }

    // A registered session is a disposable mirror of the extension's session storage.
    // All session mutations and connection bindings are serialized by _sessionGate.
    public BrowserSyncAcknowledgement RegisterAgent(string connectionId, BrowserAgentRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(registration.AgentId)
            || registration.AgentId.Length > 128)
            throw new InvalidOperationException("invalid_agent_id");
        if (registration.ProtocolVersion != BrowserCommanderProtocol.Version)
            throw new InvalidOperationException("protocol_incompatible: update BrowserCommander extension");
        if (registration.StateRevision < 0)
            throw new InvalidOperationException("invalid_state_revision");

        var tabs = ValidateTabs(registration.Tabs);
        BrowserAgentSession session;
        lock (_sessionGate)
        {
            if (_agentIdsByConnection.TryGetValue(connectionId, out var previousId)
                && previousId != registration.AgentId)
                throw new InvalidOperationException("connection_already_registered");

            if (_agentsById.TryGetValue(registration.AgentId, out var existing)
                && existing.ConnectionId != connectionId)
            {
                _agentIdsByConnection.TryRemove(existing.ConnectionId, out _);
                FailPendingCommands(existing.ConnectionId, "session_replaced");
                _logger.LogInformation("Browser agent {AgentId} taken over by {ConnectionId} from {PreviousConnection}.",
                    registration.AgentId, connectionId, existing.ConnectionId);
            }

            session = BrowserAgentSession.FromRegistration(
                registration.AgentId, connectionId, registration, DateTimeOffset.UtcNow);
            session.Tabs = tabs;
            session.StateRevision = registration.StateRevision;
            session.ConnectionGeneration = ++_nextGeneration;
            _agentsById[registration.AgentId] = session;
            _agentIdsByConnection[connectionId] = registration.AgentId;
        }

        _logger.LogInformation("Registered agent {AgentId} connection {ConnectionId} generation {Generation} revision {Revision} tabs {Count}.",
            session.AgentId, connectionId, session.ConnectionGeneration, session.StateRevision, session.Tabs.Count);
        return Acknowledge(session.StateRevision);
    }

    public BrowserSyncAcknowledgement UpdateTabs(string connectionId, BrowserAgentTabsUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        var tabs = ValidateTabs(update.Tabs);
        lock (_sessionGate)
        {
            if (!TryGetCurrentSession(connectionId, update.AgentId, out var session))
                throw new InvalidOperationException("agent_disconnected");
            if (update.StateRevision < session.StateRevision)
                throw new InvalidOperationException($"stale_revision: current={session.StateRevision}");
            if (update.StateRevision == session.StateRevision)
            {
                if (!SameTabs(session.Tabs, tabs))
                    throw new InvalidOperationException("state_conflict: identical revision has different tabs");
                return Acknowledge(session.StateRevision);
            }

            var removed = session.Tabs.Select(tab => tab.TabId)
                .Except(tabs.Select(tab => tab.TabId)).ToHashSet();
            session.Tabs = tabs;
            session.StateRevision = update.StateRevision;
            session.LastSeenAtUtc = DateTimeOffset.UtcNow;
            foreach (var pending in _pendingCommands.Values.Where(p =>
                p.ConnectionId == connectionId && removed.Contains(p.Command.TabId)))
            {
                pending.Completion.TrySetResult(CreateFailureResult(
                    pending.Command, BrowserCommandErrorCodes.TabNotAuthorized, "Tab authorization was revoked."));
            }

            return Acknowledge(session.StateRevision);
        }
    }

    public void CompleteCommand(string connectionId, BrowserAutomationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(result.CommandId))
            throw new InvalidOperationException("invalid_command_id");

        lock (_sessionGate)
        {
            if (!_pendingCommands.TryGetValue(result.CommandId, out var pending))
            {
                CleanupCompletedCommands();
                if (_recentlyCompletedCommands.TryGetValue(result.CommandId, out var completed))
                    _logger.LogWarning("Received late result for command {CommandId} from {ConnectionId}. OriginalOutcome={Outcome}",
                        result.CommandId, connectionId, completed.Outcome);
                else
                    _logger.LogWarning("Rejected unknown completion {CommandId} from {ConnectionId}",
                        result.CommandId, connectionId);
                return;
            }

            if (!TryGetCurrentSession(connectionId, result.AgentId, out var current)
                || pending.ConnectionId != connectionId
                || pending.Generation != current.ConnectionGeneration
                || pending.Command.AgentId != result.AgentId
                || pending.Command.TabId != result.TabId
                || pending.Command.Action != result.Action
                || !current.Tabs.Any(tab => tab.TabId == result.TabId))
            {
                _logger.LogWarning("Rejected completion {CommandId} from wrong or obsolete connection {ConnectionId}.",
                    result.CommandId, connectionId);
                throw new InvalidOperationException("invalid_command_completion");
            }

            current.LastSeenAtUtc = DateTimeOffset.UtcNow;
            pending.Completion.TrySetResult(result);
        }
    }

    public void RemoveConnection(string connectionId)
    {
        lock (_sessionGate)
        {
            if (!_agentIdsByConnection.TryRemove(connectionId, out var agentId))
                return;

            if (_agentsById.TryGetValue(agentId, out var session) && session.ConnectionId == connectionId)
                _agentsById.TryRemove(agentId, out _);

            FailPendingCommands(connectionId, BrowserCommandErrorCodes.AgentDisconnected);
            _logger.LogInformation("Disconnected agent {AgentId} on connection {ConnectionId}.", agentId, connectionId);
        }
    }

    public IReadOnlyCollection<BrowserAgentStatus> GetAgents()
    {
        lock (_sessionGate)
        {
            return _agentsById.Values
                .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
                .Select(agent => new BrowserAgentStatus
                {
                    AgentId = agent.AgentId,
                    ReportedAgentId = agent.ReportedAgentId,
                    ConnectionId = agent.ConnectionId,
                    ExtensionId = agent.ExtensionId,
                    BrowserName = agent.BrowserName,
                    UserAgent = agent.UserAgent,
                    ProtocolVersion = agent.ProtocolVersion,
                    DefaultCommandTimeoutMs = agent.DefaultCommandTimeoutMs,
                    Capabilities = CloneCapabilities(agent.Capabilities),
                    ConnectedAtUtc = agent.ConnectedAtUtc,
                    LastSeenAtUtc = agent.LastSeenAtUtc,
                    StateRevision = agent.StateRevision,
                    ConnectionGeneration = agent.ConnectionGeneration,
                    Tabs = CloneTabs(agent.Tabs)
                }).ToArray();
        }
    }

    public IReadOnlyCollection<BrowserPageSummary> GetPages()
    {
        lock (_sessionGate)
        {
            return _agentsById.Values
                .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
                .SelectMany(agent => agent.Tabs.Select(tab => new BrowserPageSummary
                {
                    PageId = BrowserPageRef.CreatePageId(agent.AgentId, tab.TabId),
                    AgentId = agent.AgentId,
                    ReportedAgentId = agent.ReportedAgentId,
                    BrowserName = agent.BrowserName,
                    TabId = tab.TabId,
                    WindowId = tab.WindowId,
                    Active = tab.Active,
                    Title = tab.Title,
                    Url = tab.Url
                })).OrderBy(page => page.AgentId, StringComparer.Ordinal)
                .ThenBy(page => page.TabId).ToArray();
        }
    }

    private bool TryGetCurrentSession(string connectionId, string? agentId, out BrowserAgentSession session)
    {
        session = null!;
        return !string.IsNullOrWhiteSpace(agentId)
            && _agentIdsByConnection.TryGetValue(connectionId, out var boundAgentId)
            && boundAgentId == agentId
            && _agentsById.TryGetValue(boundAgentId, out session!)
            && session.ConnectionId == connectionId;
    }

    private void FailPendingCommands(string connectionId, string errorCode)
    {
        foreach (var pending in _pendingCommands.Values.Where(p => p.ConnectionId == connectionId))
            pending.Completion.TrySetResult(CreateFailureResult(
                pending.Command, errorCode, "Browser session disconnected or replaced."));
    }

    private static BrowserSyncAcknowledgement Acknowledge(long revision) => new()
    {
        ServerInstanceId = ServerIdentity.InstanceId,
        StateRevision = revision
    };

    private static List<BrowserTabDescriptor> ValidateTabs(List<BrowserTabDescriptor>? tabs)
    {
        if (tabs is null || tabs.Count > BrowserCommanderProtocol.MaxAuthorizedTabs)
            throw new InvalidOperationException("authorization_limit_reached_or_invalid_snapshot");
        if (tabs.Any(tab => tab is null || tab.TabId <= 0 || tab.WindowId < 0)
            || tabs.Select(tab => tab.TabId).Distinct().Count() != tabs.Count)
            throw new InvalidOperationException("invalid_tab_snapshot");
        return CloneTabs(tabs).OrderBy(tab => tab.TabId).ToList();
    }

    private static bool SameTabs(List<BrowserTabDescriptor> left, List<BrowserTabDescriptor> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.TabId == pair.Second.TabId
            && pair.First.WindowId == pair.Second.WindowId
            && pair.First.Active == pair.Second.Active
            && pair.First.Url == pair.Second.Url
            && pair.First.Title == pair.Second.Title);

    public async Task<BrowserAutomationResult> ExecuteCommandAsync(BrowserAutomationCommand command, CancellationToken cancellationToken)
    {
        BrowserAgentSession agent;
        PendingCommand pendingCommand;
        lock (_sessionGate)
        {
        if (!_agentsById.TryGetValue(command.AgentId, out agent!))
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.AgentNotFound,
                $"Browser agent '{command.AgentId}' is not connected.");
        }

        if (!agent.Tabs.Any(tab => tab.TabId == command.TabId))
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.TabNotAuthorized,
                $"Tab {command.TabId} on agent '{command.AgentId}' is not authorized.");
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
            command.TimeoutMs = BrowserCommandDefaults.TimeoutMs;
        }

        if (string.IsNullOrWhiteSpace(command.CommandId))
        {
            command.CommandId = Guid.NewGuid().ToString("N");
        }

        pendingCommand = new PendingCommand(agent.ConnectionId, agent.ConnectionGeneration, command);
        if (!_pendingCommands.TryAdd(command.CommandId, pendingCommand))
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.ExecutionFailed,
                $"Command '{command.CommandId}' is already pending.");
        }

        }

        var stopwatch = Stopwatch.StartNew();
        var completionRecord = CompletedCommandRecord.Create(
            DateTimeOffset.UtcNow,
            BrowserCommandErrorCodes.ExecutionFailed);

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(command.TimeoutMs);

            _logger.LogInformation(
                "Dispatching command {CommandId} action {Action} to agent {AgentId} tab {TabId} on connection {ConnectionId} with timeout {TimeoutMs} ms.",
                command.CommandId,
                command.Action,
                command.AgentId,
                command.TabId,
                agent.ConnectionId,
                command.TimeoutMs);

            await _hubContext.Clients.Client(agent.ConnectionId)
                .SendAsync(BrowserCommanderHubMethods.ExecuteCommand, command, cancellationToken);

            _logger.LogInformation(
                "Command {CommandId} was sent to agent {AgentId}; waiting for completion.",
                command.CommandId,
                command.AgentId);

            var result = await pendingCommand.Completion.Task.WaitAsync(timeoutCts.Token);
            completionRecord = CompletedCommandRecord.FromResult(DateTimeOffset.UtcNow, result);

            _logger.LogInformation(
                "Command {CommandId} completed after {ElapsedMs} ms. Success={Success}, ErrorCode={ErrorCode}.",
                command.CommandId,
                stopwatch.ElapsedMilliseconds,
                result.Success,
                result.ErrorCode);

            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            completionRecord = CompletedCommandRecord.Create(
                DateTimeOffset.UtcNow,
                BrowserCommandErrorCodes.Timeout);

            _logger.LogWarning(
                "Command {CommandId} timed out after {ElapsedMs} ms while waiting for browser agent {AgentId}.",
                command.CommandId,
                stopwatch.ElapsedMilliseconds,
                command.AgentId);

            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.Timeout,
                $"Timed out after {command.TimeoutMs} ms.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            completionRecord = CompletedCommandRecord.Create(
                DateTimeOffset.UtcNow,
                BrowserCommandErrorCodes.RequestAborted);

            _logger.LogInformation(
                "Command {CommandId} was aborted by the caller after {ElapsedMs} ms. AgentId={AgentId}, TabId={TabId}, Action={Action}.",
                command.CommandId,
                stopwatch.ElapsedMilliseconds,
                command.AgentId,
                command.TabId,
                command.Action);

            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.RequestAborted,
                "The caller canceled the request before the browser command completed.");
        }
        catch (Exception exception)
        {
            completionRecord = CompletedCommandRecord.Create(
                DateTimeOffset.UtcNow,
                BrowserCommandErrorCodes.ExecutionFailed);

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
            _recentlyCompletedCommands[command.CommandId] = completionRecord;
            CleanupCompletedCommands();
        }
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
            if (entry.Value.CompletedAtUtc < cutoff)
            {
                _recentlyCompletedCommands.TryRemove(entry.Key, out _);
            }
        }
    }

    private sealed record CompletedCommandRecord(
        DateTimeOffset CompletedAtUtc,
        string Outcome)
    {
        public static CompletedCommandRecord Create(DateTimeOffset completedAtUtc, string outcome)
        {
            return new CompletedCommandRecord(completedAtUtc, outcome);
        }

        public static CompletedCommandRecord FromResult(DateTimeOffset completedAtUtc, BrowserAutomationResult result)
        {
            return new CompletedCommandRecord(
                completedAtUtc,
                result.Success
                    ? "completed"
                    : result.ErrorCode ?? BrowserCommandErrorCodes.ExecutionFailed);
        }
    }

    private sealed class BrowserAgentSession
    {
        public string AgentId { get; private init; } = string.Empty;

        public string ReportedAgentId { get; set; } = string.Empty;

        public string ConnectionId { get; set; } = string.Empty;

        public string? ExtensionId { get; set; }

        public string? BrowserName { get; set; }

        public string? UserAgent { get; set; }

        public string? ProtocolVersion { get; set; }

        public int DefaultCommandTimeoutMs { get; set; } = BrowserCommandDefaults.TimeoutMs;

        public BrowserAgentCapabilities Capabilities { get; set; } = new();

        public DateTimeOffset ConnectedAtUtc { get; private init; }

        public DateTimeOffset LastSeenAtUtc { get; set; }

        public List<BrowserTabDescriptor> Tabs { get; set; } = [];

        public long StateRevision { get; set; }

        public long ConnectionGeneration { get; set; }

        public static BrowserAgentSession FromRegistration(
            string agentId,
            string connectionId,
            BrowserAgentRegistration registration,
            DateTimeOffset now)
        {
            return new BrowserAgentSession
            {
                AgentId = agentId,
                ReportedAgentId = registration.AgentId,
                ConnectionId = connectionId,
                ExtensionId = registration.ExtensionId,
                BrowserName = registration.BrowserName,
                UserAgent = registration.UserAgent,
                ProtocolVersion = registration.ProtocolVersion,
                DefaultCommandTimeoutMs = registration.DefaultCommandTimeoutMs > 0
                    ? registration.DefaultCommandTimeoutMs
                    : BrowserCommandDefaults.TimeoutMs,
                Capabilities = CloneCapabilities(registration.Capabilities),
                ConnectedAtUtc = now,
                LastSeenAtUtc = now,
                Tabs = CloneTabs(registration.Tabs)
            };
        }
    }

    private sealed class PendingCommand
    {
        public PendingCommand(string connectionId, long generation, BrowserAutomationCommand command)
        {
            ConnectionId = connectionId;
            Generation = generation;
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

        public long Generation { get; }

        public BrowserAutomationCommand Command { get; }

        public TaskCompletionSource<BrowserAutomationResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
