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
    private readonly ConcurrentDictionary<(string AgentId, int TabId), byte> _authorizedTabs = new();
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
        var resolvedAgentId = ResolveOrCreateAgentId(connectionId, registration.AgentId);

        var session = _agentsById.AddOrUpdate(
            resolvedAgentId,
            _ => BrowserAgentSession.FromRegistration(resolvedAgentId, connectionId, registration, now),
            (_, existing) =>
            {
                existing.ConnectionId = connectionId;
                existing.ReportedAgentId = registration.AgentId;
                existing.ExtensionId = registration.ExtensionId;
                existing.BrowserName = registration.BrowserName;
                existing.UserAgent = registration.UserAgent;
                existing.ProtocolVersion = registration.ProtocolVersion;
                existing.DefaultCommandTimeoutMs = registration.DefaultCommandTimeoutMs > 0
                    ? registration.DefaultCommandTimeoutMs
                    : BrowserCommandDefaults.TimeoutMs;
                existing.Capabilities = CloneCapabilities(registration.Capabilities);
                existing.LastSeenAtUtc = now;
                existing.Tabs = CloneTabs(registration.Tabs);
                return existing;
            });

        _agentIdsByConnection[connectionId] = resolvedAgentId;

        if (!string.Equals(resolvedAgentId, registration.AgentId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Browser agent id collision detected. Reported agent {ReportedAgentId} on connection {ConnectionId} was assigned server session id {ResolvedAgentId}.",
                registration.AgentId,
                connectionId,
                resolvedAgentId);
        }

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
            _logger.LogInformation(
                "Received completion result for command {CommandId} from connection {ConnectionId}. Success={Success}, ErrorCode={ErrorCode}.",
                result.CommandId,
                connectionId,
                result.Success,
                result.ErrorCode);

            if (pendingCommand.Completion.TrySetResult(result))
            {
                return;
            }

            _logger.LogWarning(
                "Completion result for command {CommandId} from connection {ConnectionId} arrived after the pending task had already been completed.",
                result.CommandId,
                connectionId);
        }

        CleanupCompletedCommands();
        if (_recentlyCompletedCommands.TryRemove(result.CommandId, out var completedRecord))
        {
            var lateByMilliseconds = Math.Max(
                0,
                (long)(DateTimeOffset.UtcNow - completedRecord.CompletedAtUtc).TotalMilliseconds);

            _logger.LogWarning(
                "Received late result for command {CommandId} from connection {ConnectionId}. OriginalOutcome={OriginalOutcome}, LateByMs={LateByMs}, ResultSuccess={Success}, ResultErrorCode={ErrorCode}.",
                result.CommandId,
                connectionId,
                completedRecord.Outcome,
                lateByMilliseconds,
                result.Success,
                result.ErrorCode);
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
            _logger.LogWarning(
                "Completing pending command {CommandId} as {ErrorCode} because connection {ConnectionId} disconnected.",
                pendingPair.Command.CommandId,
                BrowserCommandErrorCodes.AgentDisconnected,
                connectionId);

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
                Tabs = CloneTabs(agent.Tabs)
            })
            .ToArray();
    }

    public IReadOnlyCollection<BrowserPageSummary> GetPages()
    {
        return _agentsById.Values
            .OrderBy(agent => agent.AgentId, StringComparer.Ordinal)
            .SelectMany(agent => agent.Tabs
                .Where(tab => _authorizedTabs.ContainsKey((agent.AgentId, tab.TabId)))
                .Select(tab => new BrowserPageSummary
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
                }))
            .OrderBy(page => page.AgentId, StringComparer.Ordinal)
            .ThenBy(page => page.TabId)
            .ToArray();
    }

    public void AuthorizeTab(string agentId, int tabId)
    {
        if (!string.IsNullOrWhiteSpace(agentId) && tabId > 0)
        {
            _authorizedTabs[(agentId, tabId)] = 0;
        }
    }

    public void RevokeTab(string agentId, int tabId)
    {
        _authorizedTabs.TryRemove((agentId, tabId), out _);
    }

    public void ClearAllAuthorizations()
    {
        _authorizedTabs.Clear();
    }

    public IReadOnlyCollection<int> GetAuthorizedTabIds(string agentId)
    {
        return _authorizedTabs.Keys
            .Where(k => string.Equals(k.AgentId, agentId, StringComparison.Ordinal))
            .Select(k => k.TabId)
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

        if (!_authorizedTabs.ContainsKey((command.AgentId, command.TabId)))
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

        var pendingCommand = new PendingCommand(agent.ConnectionId, command);
        if (!_pendingCommands.TryAdd(command.CommandId, pendingCommand))
        {
            return CreateFailureResult(
                command,
                BrowserCommandErrorCodes.ExecutionFailed,
                $"Command '{command.CommandId}' is already pending.");
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

    private string? ResolveAgentId(string connectionId, string? declaredAgentId)
    {
        if (_agentIdsByConnection.TryGetValue(connectionId, out var mappedAgentId))
        {
            return mappedAgentId;
        }

        if (!string.IsNullOrWhiteSpace(declaredAgentId)
            && _agentsById.TryGetValue(declaredAgentId, out var session)
            && string.Equals(session.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            return declaredAgentId;
        }

        return null;
    }

    private string ResolveOrCreateAgentId(string connectionId, string requestedAgentId)
    {
        if (_agentIdsByConnection.TryGetValue(connectionId, out var existingAgentId))
        {
            return existingAgentId;
        }

        if (!_agentsById.TryGetValue(requestedAgentId, out var existingSession)
            || string.Equals(existingSession.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            return requestedAgentId;
        }

        return CreateCollisionAgentId(requestedAgentId, connectionId);
    }

    private string CreateCollisionAgentId(string requestedAgentId, string connectionId)
    {
        var baseAgentId = $"{requestedAgentId}~{connectionId}";
        var candidateAgentId = baseAgentId;
        var suffix = 2;

        while (_agentsById.TryGetValue(candidateAgentId, out var existingSession)
               && !string.Equals(existingSession.ConnectionId, connectionId, StringComparison.Ordinal))
        {
            candidateAgentId = $"{baseAgentId}-{suffix}";
            suffix++;
        }

        return candidateAgentId;
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
