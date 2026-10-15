const RECORD_SEPARATOR = String.fromCharCode(0x1e);
const SESSION_STATE_KEY = 'browserCommander.authorizationState';
const SERVER_ADDRESS_KEY = 'browserCommander.serverAddress';
const COMMAND_TIMEOUT_KEY = 'browserCommander.commandTimeoutMs';
const DEBUGGER_PROTOCOL_VERSION = '1.3';
const DEBUGGER_BUFFER_LIMIT = 200;
const PACKAGED_DEFAULT_COMMAND_TIMEOUT_MS = 30000;
const BROWSER_COMMANDER_PROTOCOL_VERSION = '3';
const MAX_AUTHORIZED_TABS = 64;
const ACTION_DEFAULT_TITLE = 'BrowserCommander';
const ACTION_AUTHORIZED_TITLE = 'BrowserCommander is authorized for this tab.';
const ACTION_AUTHORIZED_BADGE_TEXT = 'ON';
const ACTION_AUTHORIZED_BADGE_BACKGROUND_COLOR = '#2563EB';
const RECONNECT_ALARM = 'browserCommander.reconnect';
const STORAGE_RETRY_ALARM = 'browserCommander.storageRetry';
const debuggerSessions = new Map();

const state = {
  agentId: null,
  stateRevision: 0,
  authorizedTabRecords: [],
  authorizedTabIds: [],
  blockedTabIds: new Set(),
  initializePromise: null,
  initialized: false,
  initializeError: null,
  mutationQueue: Promise.resolve(),
  connectionGeneration: 0,
  currentConnection: null,
  connectPromise: null,
  connected: false,
  connectionStatus: 'Disconnected',
  syncStatus: 'Pending',
  lastAcknowledgedRevision: -1,
  serverInstanceId: null,
  lastSyncAt: null,
  lastErrorCode: null,
  lastError: null,
  browserName: null,
  serverAddress: null,
  defaultServerAddress: null,
  commandTimeout: null,
  defaultCommandTimeout: null,
  reconnectTimer: null,
  reconnectAttempts: 0,
  metadataTimer: null,
  publishRequested: false,
  publishPromise: null,
  tabIndicatorOverrideTabIds: [],
  pendingCommandResults: [],
  flushingPendingCommandResults: false
};

chrome.debugger.onEvent.addListener((source, method, params) => {
  const session = debuggerSessions.get(source?.tabId);
  if (session) handleDebuggerEvent(session, method, params);
});
chrome.debugger.onDetach.addListener((source, reason) => {
  if (Number.isInteger(source?.tabId)) debuggerSessions.delete(source.tabId);
  console.warn('Debugger detached:', reason);
});
chrome.runtime.onInstalled.addListener(async () => {
  try { await bootstrapAgent(); } catch (error) { console.warn('Bootstrap failed.', error); }
  await chrome.tabs.create({ url: chrome.runtime.getURL('index.html') });
});
chrome.runtime.onStartup.addListener(() => { void bootstrapAgent().catch(recordError); });
chrome.alarms.onAlarm.addListener(alarm => {
  if (alarm.name === RECONNECT_ALARM) void ensureConnected().catch(recordError);
  if (alarm.name === STORAGE_RETRY_ALARM) void retryFailedRevocations();
});
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  const supported = new Set(['wake', 'status', 'getExtensionSettings', 'saveExtensionSettings',
    'authorizeTab', 'revokeTab', 'clearAuthorizedTabs', 'getGlobalPages']);
  if (!supported.has(message?.type)) return false;
  void (async () => {
    try {
      if (message.type === 'getExtensionSettings') {
        sendResponse(await getExtensionSettings());
        return;
      }
      await bootstrapAgent();
      if (message.type === 'getGlobalPages') {
        sendResponse(await getGlobalPages());
        return;
      }
      switch (message.type) {
        case 'saveExtensionSettings': await saveExtensionSettings(message); break;
        case 'authorizeTab': await authorizeTab(message.tabId); break;
        case 'revokeTab': await revokeTab(message.tabId); break;
        case 'clearAuthorizedTabs': await clearAuthorizedTabs(); break;
      }
      sendResponse(await createStatusResponse());
    } catch (error) {
      recordError(error);
      sendResponse({ ...(await createStatusResponse(true)), ok: false, error: String(error),
        errorCode: error?.code ?? (state.initializeError ? 'storage_failed' : 'operation_failed') });
    }
  })();
  return true;
});
chrome.tabs.onActivated.addListener(() => { scheduleMetadataPublish(); });
chrome.tabs.onCreated.addListener(() => { scheduleMetadataPublish(); });
chrome.tabs.onUpdated.addListener(() => { scheduleMetadataPublish(); });
chrome.tabs.onRemoved.addListener(tabId => {
  state.blockedTabIds.add(tabId);
  forgetTabIndicatorOverride(tabId);
  void removeMissingAuthorizedTabs([tabId]).catch(recordError);
  void detachDebuggerSession(tabId);
});
void bootstrapAgent().catch(recordError);

function recordError(error) {
  state.lastError = String(error);
  state.lastErrorCode = error?.code ?? 'operation_failed';
  console.warn('BrowserCommander:', error);
}

function createError(code, message) {
  const error = new Error(message);
  error.code = code;
  return error;
}

async function bootstrapAgent() {
  if (state.initialized) return;
  if (state.initializeError) throw state.initializeError;
  if (state.initializePromise) return state.initializePromise;
  state.initializePromise = (async () => {
    try {
      const stored = (await chrome.storage.session.get(SESSION_STATE_KEY))[SESSION_STATE_KEY];
      if (stored !== undefined && (!stored || stored.schemaVersion !== 1
        || typeof stored.sessionAgentId !== 'string' || !stored.sessionAgentId
        || !Number.isSafeInteger(stored.stateRevision) || stored.stateRevision < 0
        || !Array.isArray(stored.authorizedTabs)
        || stored.authorizedTabs.length > MAX_AUTHORIZED_TABS
        || stored.authorizedTabs.some(tab => !Number.isInteger(tab.tabId) || tab.tabId <= 0
          || !Number.isInteger(tab.windowId) || tab.windowId < 0)
        || new Set(stored.authorizedTabs.map(tab => tab.tabId)).size !== stored.authorizedTabs.length)) {
        throw createError('storage_failed', 'Invalid authorization state in session storage.');
      }
      const records = stored?.authorizedTabs ?? [];
      const existing = await chrome.tabs.query({});
      const existingById = new Map(existing.map(tab => [tab.id, tab]));
      const retained = records.filter(tab =>
        existingById.has(tab.tabId) && existingById.get(tab.tabId).windowId === tab.windowId
        && !state.blockedTabIds.has(tab.tabId));
      const next = {
        schemaVersion: 1,
        sessionAgentId: stored?.sessionAgentId ?? crypto.randomUUID(),
        stateRevision: (stored?.stateRevision ?? 0) + (retained.length !== records.length ? 1 : 0),
        authorizedTabs: retained
      };
      if (!stored || retained.length !== records.length)
        await chrome.storage.session.set({ [SESSION_STATE_KEY]: next });
      state.agentId = next.sessionAgentId;
      state.stateRevision = next.stateRevision;
      state.authorizedTabRecords = next.authorizedTabs;
      state.authorizedTabIds = normalizeTabIds(next.authorizedTabs.map(tab => tab.tabId));
      state.commandTimeout = await getConfiguredCommandTimeout();
      state.initialized = true;
      await syncAuthorizedTabIndicators();
      void ensureDebuggerSessionsForAuthorizedTabs();
      void ensureConnected().catch(recordError);
    } catch (error) {
      state.initializeError = createError('storage_failed', String(error));
      throw state.initializeError;
    }
  })();
  return state.initializePromise;
}

// All read/modify/write operations share a single queue; failed writes do not publish memory.
function enqueueMutation(transform) {
  const work = state.mutationQueue.catch(() => {}).then(async () => {
    await bootstrapAgent();
    const before = state.authorizedTabRecords;
    const after = await transform([...before]);
    if (after === null) return false;
    if (after.length > MAX_AUTHORIZED_TABS)
      throw createError('authorization_limit_reached', 'The limit of 64 authorized tabs has been reached.');
    const revision = state.stateRevision + 1;
    const next = {
      schemaVersion: 1, sessionAgentId: state.agentId,
      stateRevision: revision, authorizedTabs: after
    };
    await chrome.storage.session.set({ [SESSION_STATE_KEY]: next });
    state.authorizedTabRecords = after;
    state.authorizedTabIds = normalizeTabIds(after.map(tab => tab.tabId));
    state.stateRevision = revision;
    await syncAuthorizedTabIndicators();
    void publishTabs();
    return true;
  });
  state.mutationQueue = work;
  return work;
}

async function ensureConnected() {
  await bootstrapAgent();
  if (state.currentConnection?.socket.readyState === WebSocket.OPEN && state.connected) return;
  if (state.connectPromise) return state.connectPromise;
  const promise = connectCore();
  state.connectPromise = promise;
  try { await promise; } finally {
    if (state.connectPromise === promise) state.connectPromise = null;
  }
}

async function connectCore() {
  clearReconnectTimer();
  state.connectionStatus = 'Connecting';
  const generation = ++state.connectionGeneration;
  try {
    const serverAddress = await getServerAddress();
    const response = await fetch(`${serverAddress}/browserCommanderHub/negotiate?negotiateVersion=1`,
      { method: 'POST' });
    if (!response.ok) throw createError('server_unavailable', `SignalR negotiate returned ${response.status}`);
    const payload = await response.json();
    if (!payload.connectionToken) throw createError('protocol_incompatible', 'No SignalR connection token.');
    if (generation !== state.connectionGeneration) return;
    const socket = new WebSocket(createWebSocketUrl(serverAddress, payload.connectionToken));
    const connection = { socket, generation, buffer: '', pending: new Map(), invocationId: 0,
      registered: false, acknowledgedRevision: -1, acknowledgedSnapshot: null, closed: false };
    state.currentConnection = connection;
    await new Promise((resolve, reject) => {
      let handshaken = false;
      const timer = setTimeout(() => reject(createError('protocol_incompatible', 'SignalR handshake timeout.')), 10000);
      const valid = () => state.currentConnection === connection && generation === state.connectionGeneration;
      socket.addEventListener('open', () => {
        if (valid()) socket.send(JSON.stringify({ protocol: 'json', version: 1 }) + RECORD_SEPARATOR);
      });
      socket.addEventListener('message', event => {
        if (!valid()) return;
        try {
          for (const message of parseHubMessages(event.data, connection)) {
            if (!handshaken) {
              if (message.type !== undefined || message.error)
                throw createError('protocol_incompatible', message.error ?? 'Invalid SignalR handshake response.');
              handshaken = true;
              clearTimeout(timer);
              state.connected = true;
              state.connectionStatus = 'Connected';
              state.syncStatus = 'Registering';
              state.reconnectAttempts = 0;
              startKeepAlive(connection);
              resolve();
            } else if (message.type === 3) {
              const pending = connection.pending.get(message.invocationId);
              if (pending) {
                connection.pending.delete(message.invocationId);
                clearTimeout(pending.timeout);
                if (message.error) pending.reject(createError('sync_failed', message.error));
                else pending.resolve(message.result);
              }
            } else if (message.type === 1) {
              void handleHubMessageSafe(message, connection);
            } else if (message.type === 7) {
              socket.close();
            } else if (message.type !== 6) {
              recordError(createError('protocol_incompatible', 'Unknown SignalR frame.'));
            }
          }
        } catch (error) {
          clearTimeout(timer);
          if (!handshaken) reject(error);
          socket.close();
        }
      });
      socket.addEventListener('error', () => {
        if (!handshaken) { clearTimeout(timer); reject(createError('server_unavailable', 'WebSocket error.')); }
      });
      socket.addEventListener('close', () => {
        clearTimeout(timer);
        closeConnection(connection);
        if (!handshaken) reject(createError('server_unavailable', 'WebSocket closed before handshake.'));
      }, { once: true });
    });
    if (state.currentConnection === connection) void publishTabs();
  } catch (error) {
    if (generation === state.connectionGeneration) {
      state.connectionStatus = 'Disconnected';
      state.syncStatus = 'Pending';
      recordError(error);
      scheduleReconnect();
    }
    throw error;
  }
}

function closeConnection(connection) {
  if (connection.closed) return;
  connection.closed = true;
  for (const pending of connection.pending.values()) {
    clearTimeout(pending.timeout);
    pending.reject(createError('agent_disconnected', 'SignalR connection lost.'));
  }
  connection.pending.clear();
  if (state.currentConnection !== connection) return;
  state.currentConnection = null;
  state.connected = false;
  state.connectionStatus = 'Disconnected';
  state.syncStatus = 'Pending';
  state.lastAcknowledgedRevision = -1;
  stopKeepAlive();
  scheduleReconnect();
}

async function registerAgent(connection, snapshot, revision) {
  state.browserName ??= await detectBrowserName();
  return sendInvocation('RegisterAgent', [{
    agentId: state.agentId,
    extensionId: chrome.runtime.id,
    browserName: state.browserName,
    userAgent: navigator.userAgent,
    protocolVersion: BROWSER_COMMANDER_PROTOCOL_VERSION,
    stateRevision: revision,
    defaultCommandTimeoutMs: await getConfiguredCommandTimeout(),
    capabilities: {
      supportsPlanExecution: true, supportsContentScriptSteps: true,
      supportsDebuggerSteps: true, supportsTabSteps: true
    },
    tabs: snapshot
  }], connection);
}

function scheduleMetadataPublish() {
  if (state.metadataTimer) return;
  state.metadataTimer = setTimeout(() => {
    state.metadataTimer = null;
    void publishTabs();
  }, 90);
}

async function publishTabs() {
  state.publishRequested = true;
  if (state.publishPromise) return state.publishPromise;
  const work = (async () => {
    let publishingConnection = null;
    try {
      await bootstrapAgent();
      await state.mutationQueue.catch(() => {});
      while (state.publishRequested) {
        state.publishRequested = false;
        if (!state.connected) {
          try { await ensureConnected(); } catch { return; }
        }
        const connection = state.currentConnection;
        publishingConnection = connection;
        if (!connection || connection.socket.readyState !== WebSocket.OPEN) return;
        let snapshot = await getAuthorizedTabsSnapshot();
        let revision = state.stateRevision;
        let signature = JSON.stringify(snapshot);
        if (connection.registered && revision <= connection.acknowledgedRevision
          && signature !== connection.acknowledgedSnapshot) {
          await enqueueMutation(records => records);
          snapshot = await getAuthorizedTabsSnapshot();
          revision = state.stateRevision;
          signature = JSON.stringify(snapshot);
        }
        state.syncStatus = connection.registered ? 'Pending' : 'Registering';
        const ack = connection.registered
          ? await sendInvocation('UpdateTabs', [{
              agentId: state.agentId, stateRevision: revision, tabs: snapshot
            }], connection)
          : await registerAgent(connection, snapshot, revision);
        if (state.currentConnection !== connection) return;
        if (!ack?.accepted || ack.stateRevision !== revision || !ack.serverInstanceId)
          throw createError('sync_failed', 'Invalid synchronization acknowledgement.');
        if (state.serverInstanceId && state.serverInstanceId !== ack.serverInstanceId)
          connection.registered = false;
        state.serverInstanceId = ack.serverInstanceId;
        connection.acknowledgedRevision = revision;
        connection.acknowledgedSnapshot = signature;
        connection.registered = true;
        state.lastAcknowledgedRevision = revision;
        state.lastSyncAt = new Date().toISOString();
        if (revision === state.stateRevision) state.syncStatus = 'Synced';
        else { state.syncStatus = 'Pending'; state.publishRequested = true; }
        await flushPendingCommandResults();
      }
    } catch (error) {
      recordError(error);
      // A failed invocation belongs to its original transport, not to a reconnect
      // that may have taken over while the old request was awaiting its Completion.
      if (publishingConnection && state.currentConnection === publishingConnection) {
        state.syncStatus = 'Failed';
        publishingConnection.socket.close();
        closeConnection(publishingConnection);
      }
    }
  })();
  state.publishPromise = work;
  try { await work; } finally {
    if (state.publishPromise === work) state.publishPromise = null;
    if (state.publishRequested) void publishTabs();
  }
}

async function handleHubMessageSafe(message, connection) {
  try {
    if (connection !== state.currentConnection || !connection.registered) return;
    if (message.target === 'ExecuteCommand' && message.arguments?.[0]) {
      await executeCommandAndReportResult(message.arguments[0], connection);
    }
    // RefreshAuthorizations is an obsolete server-to-browser message, intentionally ignored.
  } catch (error) { recordError(error); }
}

async function executeCommandAndReportResult(command, connection) {
  let result;
  try { result = await executeCommand(command); }
  catch (error) { result = createUnexpectedCommandFailureResult(command, error); }
  try {
    if (state.currentConnection === connection) await sendCommandCompletion(result, command, connection);
  } catch (error) { recordError(error); }
}

async function executeCommand(command) {
  const normalizedAction = normalizeAction(command?.action);
  const baseResult = createBaseResult(command, normalizedAction);

  const validationError = validateCommand(command, normalizedAction);
  if (validationError) {
    return {
      ...baseResult,
      errorCode: 'validation_failed',
      error: validationError
    };
  }

  if (!isTabAuthorized(command.tabId)) {
    return {
      ...baseResult,
      errorCode: 'tab_not_authorized',
      error: `Tab ${command.tabId} is not authorized by the browser user.`
    };
  }

  try {
    const tab = await getTabOrThrow(command.tabId);
    const grant = state.authorizedTabRecords.find(record => record.tabId === command.tabId);
    if (!grant || grant.windowId !== tab.windowId)
      throw createError('tab_not_authorized', 'Tab identity changed since authorization.');

    if (normalizedAction === 'executePlan') {
      return await executePlanCommand(command, baseResult);
    }

    if (isPageAction(normalizedAction)) {
      return await executePageAction(command, normalizedAction, baseResult);
    }

    if (normalizedAction === 'locatorDragTo') {
      return await executeLocatorDragTo(command, baseResult);
    }

    if (normalizedAction === 'locatorPress') {
      return await executeLocatorPress(command, baseResult);
    }

    if (isScriptAction(normalizedAction)) {
      return await executeScriptAction(command, normalizedAction, baseResult);
    }

    return {
      ...baseResult,
      errorCode: 'unsupported_action',
      error: `Unsupported action '${command?.action}'.`
    };
  } catch (error) {
    return {
      ...baseResult,
      errorCode: getErrorCode(error),
      error: getErrorMessage(error)
    };
  }
}

async function ensureContentScript(tabId, frameId) {
  const target = frameId != null
    ? { tabId, frameIds: [frameId] }
    : { tabId };

  await chrome.scripting.executeScript({
    target,
    files: ['injectedScript.js']
  });
}

function createBaseResult(command, normalizedAction) {
  return {
    commandId: command?.commandId ?? null,
    agentId: state.agentId,
    tabId: command?.tabId ?? null,
    action: normalizedAction ?? command?.action ?? null,
    success: false,
    text: null,
    html: null,
    exists: null,
    url: null,
    title: null,
    count: null,
    visible: null,
    readyState: null,
    valueJson: null,
    screenshotBase64: null,
    errorCode: null,
    error: null
  };
}

function validateCommand(command, normalizedAction) {
  if (!command?.tabId) {
    return 'tabId is required.';
  }

  if (!normalizedAction) {
    return 'action is required.';
  }

  if (normalizedAction === 'executePlan') {
    return Array.isArray(command?.plan?.steps) && command.plan.steps.length > 0
      ? null
      : 'plan.steps is required.';
  }

  if (requiresSelector(normalizedAction) && !command?.selector) {
    return 'selector is required.';
  }

  if (requiresSourceSelector(normalizedAction) && !command?.sourceSelector) {
    return 'sourceSelector is required.';
  }

  if (requiresTargetSelector(normalizedAction) && !command?.targetSelector) {
    return 'targetSelector is required.';
  }

  if (requiresText(normalizedAction) && typeof command?.text !== 'string') {
    return 'text is required.';
  }

  if (requiresKey(normalizedAction) && typeof command?.key !== 'string') {
    return 'key is required.';
  }

  if (requiresUrl(normalizedAction) && !command?.url) {
    return 'url is required.';
  }

  if (requiresScript(normalizedAction) && !command?.script) {
    return 'script is required.';
  }

  if (requiresMouseButton(normalizedAction)
    && command?.button != null
    && !isSupportedMouseButton(command.button)) {
    return 'button must be one of: left, middle, right.';
  }

  if (requiresMoveSteps(normalizedAction)
    && command?.moveSteps != null
    && (!Number.isInteger(command.moveSteps) || command.moveSteps < 1 || command.moveSteps > 100)) {
    return 'moveSteps must be an integer between 1 and 100.';
  }

  if (requiresViewportSize(normalizedAction)) {
    if (!Number.isInteger(command?.width) || command.width <= 0) {
      return 'width must be a positive integer.';
    }

    if (!Number.isInteger(command?.height) || command.height <= 0) {
      return 'height must be a positive integer.';
    }
  }

  return null;
}

async function executePageAction(command, action, baseResult) {
  switch (action) {
    case 'pageUrl': {
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null
      };
    }
    case 'pageTitle': {
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        title: tab.title ?? null
      };
    }
    case 'pageContent': {
      const html = await executeInTab(command.tabId, command.frameId, readDocumentContent);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        html: typeof html === 'string' ? html : null,
        url: tab.url ?? null,
        title: tab.title ?? null
      };
    }
    case 'pageEvaluate': {
      const evaluation = await evaluateOnPage(command.tabId, command.script);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        valueJson: JSON.stringify(evaluation)
      };
    }
    case 'pageScreenshot': {
      const format = normalizeScreenshotFormat(command.format);
      const screenshotBase64 = await capturePageScreenshot(command.tabId, format);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        screenshotBase64
      };
    }
    case 'pageConsoleMessages': {
      const session = await ensureDebuggerSession(command.tabId);
      const entries = getBufferedEntries(session.consoleMessages, getCommandLimit(command), command.clearBuffer);
      return {
        ...baseResult,
        success: true,
        valueJson: JSON.stringify(entries)
      };
    }
    case 'pageNetworkRequests': {
      const session = await ensureDebuggerSession(command.tabId);
      const entries = getBufferedEntries(session.networkRequests, getCommandLimit(command), command.clearBuffer);
      if (command.clearBuffer) {
        session.networkRequestsById.clear();
      }

      return {
        ...baseResult,
        success: true,
        valueJson: JSON.stringify(entries)
      };
    }
    case 'pageGoto': {
      await chrome.tabs.update(command.tabId, { url: command.url });
      const readyState = await waitForLoadState(command.tabId, command.frameId, command.waitState, command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        readyState
      };
    }
    case 'pageReload': {
      await chrome.tabs.reload(command.tabId);
      const readyState = await waitForLoadState(command.tabId, command.frameId, command.waitState, command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        readyState
      };
    }
    case 'pageGoBack': {
      if (typeof chrome.tabs.goBack !== 'function') {
        throw createCommandError('unsupported_action', 'chrome.tabs.goBack is not available in this browser.');
      }

      await chrome.tabs.goBack(command.tabId);
      const readyState = await waitForLoadState(command.tabId, command.frameId, command.waitState, command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        readyState
      };
    }
    case 'pageGoForward': {
      if (typeof chrome.tabs.goForward !== 'function') {
        throw createCommandError('unsupported_action', 'chrome.tabs.goForward is not available in this browser.');
      }

      await chrome.tabs.goForward(command.tabId);
      const readyState = await waitForLoadState(command.tabId, command.frameId, command.waitState, command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        readyState
      };
    }
    case 'pageWaitForUrl': {
      const url = await waitForUrl(command.tabId, command.url, command.matchMode, command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: url ?? tab.url ?? null,
        title: tab.title ?? null
      };
    }
    case 'pageWaitForLoadState': {
      const readyState = await waitForLoadState(command.tabId, command.frameId, command.waitState, command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null,
        readyState
      };
    }
    case 'pageSetViewportSize': {
      await applyViewportSize(command.tabId, command.width, command.height);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null
      };
    }
    case 'pageClearViewportOverride': {
      await clearViewportSizeOverride(command.tabId);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        url: tab.url ?? null,
        title: tab.title ?? null
      };
    }
    default:
      throw createCommandError('unsupported_action', `Unsupported action '${action}'.`);
  }
}

async function executeScriptAction(command, action, baseResult) {
  await ensureContentScript(command.tabId, command.frameId);

  const message = {
    action,
    selector: command.selector ?? null,
    text: command.text ?? null,
    query: command.query ?? null,
    onlyVisible: command.onlyVisible !== false,
    interactiveOnly: command.interactiveOnly !== false,
    limit: getCommandLimit(command),
    waitState: command.waitState ?? null,
    timeoutMs: getCommandTimeout(command)
  };

  const options = command.frameId != null ? { frameId: command.frameId } : undefined;
  const response = await chrome.tabs.sendMessage(command.tabId, message, options);

  return {
    ...baseResult,
    success: Boolean(response?.success),
    text: response?.text ?? null,
    html: response?.html ?? null,
    exists: typeof response?.exists === 'boolean' ? response.exists : null,
    url: response?.url ?? null,
    title: response?.title ?? null,
    count: Number.isInteger(response?.count) ? response.count : null,
    visible: typeof response?.visible === 'boolean' ? response.visible : null,
    readyState: response?.readyState ?? null,
    valueJson: response?.valueJson ?? null,
    screenshotBase64: response?.screenshotBase64 ?? null,
    errorCode: response?.errorCode ?? null,
    error: response?.error ?? null
  };
}

async function executeLocatorPress(command, baseResult) {
  await focusLocator(command.tabId, command.frameId, command.selector, command.timeoutMs);
  await pressKeyOnTab(command.tabId, command.key);

  return {
    ...baseResult,
    success: true,
    exists: true,
    visible: true
  };
}

async function executeLocatorDragTo(command, baseResult) {
  await dragLocatorTo(
    command.tabId,
    command.frameId,
    command.sourceSelector,
    command.targetSelector,
    command.button,
    command.moveSteps,
    command.timeoutMs);

  const tab = await getTabOrThrow(command.tabId);
  return {
    ...baseResult,
    success: true,
    exists: true,
    visible: true,
    url: tab.url ?? null,
    title: tab.title ?? null
  };
}

async function executePlanCommand(command, baseResult) {
  const steps = Array.isArray(command?.plan?.steps)
    ? command.plan.steps
    : [];

  if (steps.length === 0) {
    return {
      ...baseResult,
      errorCode: 'validation_failed',
      error: 'plan.steps is required.'
    };
  }

  let aggregateResult = {
    ...baseResult,
    success: true
  };

  for (const step of steps) {
    if (!isTabAuthorized(command.tabId)) {
      return mergeAutomationResults(aggregateResult, {
        ...baseResult, success: false, errorCode: 'tab_not_authorized',
        error: 'The tab was revoked before the next execution-plan step.'
      });
    }
    const current = await chrome.tabs.get(command.tabId).catch(() => null);
    const grant = state.authorizedTabRecords.find(record => record.tabId === command.tabId);
    if (!current || !grant || grant.windowId !== current.windowId) {
      return mergeAutomationResults(aggregateResult, {
        ...baseResult, success: false, errorCode: 'tab_not_authorized',
        error: 'The tab was closed or replaced during execution.'
      });
    }
    const stepResult = await executePlanStep(command, step);
    aggregateResult = mergeAutomationResults(aggregateResult, stepResult);

    if (!stepResult.success) {
      return aggregateResult;
    }
  }

  return aggregateResult;
}

async function executePlanStep(command, step) {
  const normalizedKind = String(step?.kind ?? '').trim();
  const normalizedOperation = String(step?.operation ?? '').trim();
  const stepCommand = createPlanStepCommand(command, step);
  const stepBaseResult = createBaseResult(command, 'executePlan');

  switch (normalizedKind) {
    case 'contentScript':
      return executeContentStep(stepCommand, normalizedOperation, stepBaseResult);
    case 'debugger':
      return executeDebuggerStep(stepCommand, normalizedOperation, stepBaseResult);
    case 'tab':
      return executeTabStep(stepCommand, normalizedOperation, stepBaseResult);
    default:
      return {
        ...stepBaseResult,
        errorCode: 'unsupported_action',
        error: `Unsupported execution step kind '${step?.kind}'.`
      };
  }
}

async function executeContentStep(command, operation, baseResult) {
  const action = mapContentOperationToAction(operation);
  if (!action) {
    return {
      ...baseResult,
      errorCode: 'unsupported_action',
      error: `Unsupported contentScript operation '${operation}'.`
    };
  }

  return executeScriptAction(command, action, baseResult);
}

async function executeDebuggerStep(command, operation, baseResult) {
  switch (operation) {
    case 'evaluate':
      return executePageAction(command, 'pageEvaluate', baseResult);
    case 'captureScreenshot':
      return executePageAction(command, 'pageScreenshot', baseResult);
    case 'readConsoleMessages':
      return executePageAction(command, 'pageConsoleMessages', baseResult);
    case 'readNetworkRequests':
      return executePageAction(command, 'pageNetworkRequests', baseResult);
    case 'setViewportSize':
      await applyViewportSize(command.tabId, command.width, command.height);
      {
        const tab = await getTabOrThrow(command.tabId);
        return {
          ...baseResult,
          success: true,
          exists: true,
          visible: true,
          url: tab.url ?? null,
          title: tab.title ?? null
        };
      }
    case 'clearViewportOverride':
      await clearViewportSizeOverride(command.tabId);
      {
        const tab = await getTabOrThrow(command.tabId);
        return {
          ...baseResult,
          success: true,
          exists: true,
          visible: true,
          url: tab.url ?? null,
          title: tab.title ?? null
        };
      }
    case 'pressKey':
      await pressKeyOnTab(command.tabId, command.key);
      return {
        ...baseResult,
        success: true,
        exists: true,
        visible: true
      };
    case 'dragLocatorTo': {
      await dragLocatorTo(
        command.tabId,
        command.frameId,
        command.sourceSelector,
        command.targetSelector,
        command.button,
        command.moveSteps,
        command.timeoutMs);
      const tab = await getTabOrThrow(command.tabId);
      return {
        ...baseResult,
        success: true,
        exists: true,
        visible: true,
        url: tab.url ?? null,
        title: tab.title ?? null
      };
    }
    default:
      return {
        ...baseResult,
        errorCode: 'unsupported_action',
        error: `Unsupported debugger operation '${operation}'.`
      };
  }
}

async function executeTabStep(command, operation, baseResult) {
  const action = mapTabOperationToAction(operation);
  if (!action) {
    return {
      ...baseResult,
      errorCode: 'unsupported_action',
      error: `Unsupported tab operation '${operation}'.`
    };
  }

  return executePageAction(command, action, baseResult);
}

function createPlanStepCommand(command, step) {
  return {
    ...command,
    selector: step?.selector ?? command?.selector ?? null,
    sourceSelector: step?.sourceSelector ?? command?.sourceSelector ?? null,
    targetSelector: step?.targetSelector ?? command?.targetSelector ?? null,
    text: step?.text ?? command?.text ?? null,
    key: step?.key ?? command?.key ?? null,
    button: step?.button ?? command?.button ?? 'left',
    moveSteps: Number.isInteger(step?.moveSteps) ? step.moveSteps : command?.moveSteps,
    url: step?.url ?? command?.url ?? null,
    matchMode: step?.matchMode ?? command?.matchMode ?? null,
    waitState: step?.waitState ?? command?.waitState ?? null,
    script: step?.script ?? command?.script ?? null,
    query: step?.query ?? command?.query ?? null,
    onlyVisible: typeof step?.onlyVisible === 'boolean' ? step.onlyVisible : command?.onlyVisible,
    interactiveOnly: typeof step?.interactiveOnly === 'boolean' ? step.interactiveOnly : command?.interactiveOnly,
    format: step?.format ?? command?.format ?? null,
    width: Number.isInteger(step?.width) ? step.width : command?.width,
    height: Number.isInteger(step?.height) ? step.height : command?.height,
    limit: Number.isInteger(step?.limit) ? step.limit : command?.limit,
    clearBuffer: typeof step?.clearBuffer === 'boolean' ? step.clearBuffer : command?.clearBuffer,
    timeoutMs: step?.timeoutMs > 0 ? step.timeoutMs : command?.timeoutMs
  };
}

function mergeAutomationResults(baseResult, stepResult) {
  return {
    ...baseResult,
    success: Boolean(stepResult?.success),
    text: stepResult?.text ?? baseResult.text,
    html: stepResult?.html ?? baseResult.html,
    exists: typeof stepResult?.exists === 'boolean' ? stepResult.exists : baseResult.exists,
    url: stepResult?.url ?? baseResult.url,
    title: stepResult?.title ?? baseResult.title,
    count: Number.isInteger(stepResult?.count) ? stepResult.count : baseResult.count,
    visible: typeof stepResult?.visible === 'boolean' ? stepResult.visible : baseResult.visible,
    readyState: stepResult?.readyState ?? baseResult.readyState,
    valueJson: stepResult?.valueJson ?? baseResult.valueJson,
    screenshotBase64: stepResult?.screenshotBase64 ?? baseResult.screenshotBase64,
    errorCode: stepResult?.errorCode ?? null,
    error: stepResult?.error ?? null
  };
}

function mapContentOperationToAction(operation) {
  const mappings = {
    getPageContent: 'pageContent',
    findLocators: 'pageFindLocators',
    fillLocator: 'locatorFill',
    focusLocator: 'locatorFocus',
    clickLocator: 'locatorClick',
    readInnerText: 'locatorInnerText',
    readTextContent: 'locatorTextContent',
    readInnerHtml: 'locatorInnerHtml',
    readInputValue: 'locatorInputValue',
    checkExists: 'locatorExists',
    countMatches: 'locatorCount',
    checkVisible: 'locatorIsVisible',
    waitForLocator: 'locatorWaitFor'
  };

  return mappings[operation] ?? null;
}

function mapTabOperationToAction(operation) {
  const mappings = {
    getPageUrl: 'pageUrl',
    getPageTitle: 'pageTitle',
    goto: 'pageGoto',
    reload: 'pageReload',
    goBack: 'pageGoBack',
    goForward: 'pageGoForward',
    waitForUrl: 'pageWaitForUrl',
    waitForLoadState: 'pageWaitForLoadState'
  };

  return mappings[operation] ?? null;
}

function isPageAction(action) {
  return new Set([
    'pageUrl',
    'pageTitle',
    'pageContent',
    'pageEvaluate',
    'pageScreenshot',
    'pageConsoleMessages',
    'pageNetworkRequests',
    'pageGoto',
    'pageReload',
    'pageGoBack',
    'pageGoForward',
    'pageWaitForUrl',
    'pageWaitForLoadState',
    'pageSetViewportSize',
    'pageClearViewportOverride'
  ]).has(action);
}

function isScriptAction(action) {
  return new Set([
    'pageFindLocators',
    'locatorClick',
    'locatorFill',
    'locatorInnerText',
    'locatorTextContent',
    'locatorInnerHtml',
    'locatorInputValue',
    'locatorExists',
    'locatorCount',
    'locatorIsVisible',
    'locatorWaitFor',
    'setText',
    'getText',
    'getHtml',
    'click',
    'exists'
  ]).has(action);
}

function requiresSourceSelector(action) {
  return new Set([
    'locatorDragTo'
  ]).has(action);
}

function requiresTargetSelector(action) {
  return new Set([
    'locatorDragTo'
  ]).has(action);
}

function requiresSelector(action) {
  return new Set([
    'locatorClick',
    'locatorFill',
    'locatorPress',
    'locatorInnerText',
    'locatorTextContent',
    'locatorInnerHtml',
    'locatorInputValue',
    'locatorExists',
    'locatorCount',
    'locatorIsVisible',
    'locatorWaitFor',
    'setText',
    'getText',
    'getHtml',
    'click',
    'exists'
  ]).has(action);
}

function requiresText(action) {
  return new Set([
    'locatorFill',
    'setText'
  ]).has(action);
}

function requiresKey(action) {
  return new Set([
    'locatorPress'
  ]).has(action);
}

function requiresMouseButton(action) {
  return new Set([
    'locatorDragTo'
  ]).has(action);
}

function requiresMoveSteps(action) {
  return new Set([
    'locatorDragTo'
  ]).has(action);
}

function requiresUrl(action) {
  return new Set([
    'pageGoto',
    'pageWaitForUrl'
  ]).has(action);
}

function requiresScript(action) {
  return new Set([
    'pageEvaluate'
  ]).has(action);
}

function requiresViewportSize(action) {
  return new Set([
    'pageSetViewportSize'
  ]).has(action);
}

function isSupportedMouseButton(button) {
  const normalizedButton = String(button ?? '').trim().toLowerCase();
  return normalizedButton === 'left'
    || normalizedButton === 'middle'
    || normalizedButton === 'right';
}

function normalizeMouseButton(button) {
  const normalizedButton = String(button ?? 'left').trim().toLowerCase();
  switch (normalizedButton) {
    case '':
    case 'left':
      return { button: 'left', buttons: 1 };
    case 'middle':
      return { button: 'middle', buttons: 4 };
    case 'right':
      return { button: 'right', buttons: 2 };
    default:
      throw createCommandError(
        'validation_failed',
        `Unsupported button '${button}'. Supported values: left, middle, right.`
      );
  }
}

function getCommandMoveSteps(command) {
  if (command?.moveSteps == null) {
    return 12;
  }

  if (!Number.isInteger(command.moveSteps) || command.moveSteps < 1 || command.moveSteps > 100) {
    throw createCommandError('validation_failed', 'moveSteps must be an integer between 1 and 100.');
  }

  return command.moveSteps;
}

async function getTabOrThrow(tabId) {
  try {
    return await chrome.tabs.get(tabId);
  } catch {
    throw createCommandError('tab_not_found', `Tab ${tabId} was not found.`);
  }
}

async function ensureDebuggerSessionsForAuthorizedTabs() {
  const authorizedTabIds = normalizeTabIds(state.authorizedTabIds);
  const authorizedTabIdSet = new Set(authorizedTabIds);

  await Promise.allSettled(
    [...debuggerSessions.keys()]
      .filter(tabId => !authorizedTabIdSet.has(tabId))
      .map(tabId => detachDebuggerSession(tabId))
  );

  await Promise.allSettled(
    authorizedTabIds.map(tabId => tryEnsureDebuggerSession(tabId))
  );
}

async function tryEnsureDebuggerSession(tabId) {
  try {
    await ensureDebuggerSession(tabId);
  } catch (error) {
    console.warn(`Failed to initialize debugger session for tab ${tabId}.`, error);
  }
}

async function ensureDebuggerSession(tabId) {
  if (!isTabAuthorized(tabId)) {
    throw createCommandError('tab_not_authorized', `Tab ${tabId} is not authorized by the browser user.`);
  }

  const existing = debuggerSessions.get(tabId);
  if (existing?.attached) {
    return existing;
  }

  const debuggee = { tabId };

  await chrome.debugger.attach(debuggee, DEBUGGER_PROTOCOL_VERSION);

  const session = {
    tabId,
    attached: true,
    consoleMessages: [],
    networkRequests: [],
    networkRequestsById: new Map()
  };

  debuggerSessions.set(tabId, session);

  try {
    await chrome.debugger.sendCommand(debuggee, 'Runtime.enable');
    await chrome.debugger.sendCommand(debuggee, 'Log.enable');
    await chrome.debugger.sendCommand(debuggee, 'Network.enable');
    await chrome.debugger.sendCommand(debuggee, 'Page.enable');
  } catch (error) {
    debuggerSessions.delete(tabId);
    try {
      await chrome.debugger.detach(debuggee);
    } catch {
    }

    throw error;
  }

  return session;
}

async function detachDebuggerSession(tabId) {
  const session = debuggerSessions.get(tabId);
  debuggerSessions.delete(tabId);

  if (!session?.attached) {
    return;
  }

  try {
    await chrome.debugger.detach({ tabId });
  } catch {
  }
}

async function evaluateOnPage(tabId, expression) {
  await ensureDebuggerSession(tabId);
  const response = await chrome.debugger.sendCommand(
    { tabId },
    'Runtime.evaluate',
    {
      expression,
      returnByValue: true,
      awaitPromise: true,
      userGesture: true
    }
  );

  if (response?.exceptionDetails) {
    const exceptionText = response.exceptionDetails.exception?.description
      ?? response.exceptionDetails.text
      ?? 'Runtime.evaluate failed.';

    throw createCommandError('execution_failed', exceptionText);
  }

  return normalizeEvaluationResult(response?.result);
}

async function capturePageScreenshot(tabId, format) {
  await ensureDebuggerSession(tabId);
  const response = await chrome.debugger.sendCommand(
    { tabId },
    'Page.captureScreenshot',
    { format, captureBeyondViewport: true, fromSurface: true }
  );

  return response?.data ?? '';
}

async function applyViewportSize(tabId, width, height) {
  await ensureDebuggerSession(tabId);

  if (!Number.isInteger(width) || width <= 0) {
    throw createCommandError('validation_failed', 'width must be a positive integer.');
  }

  if (!Number.isInteger(height) || height <= 0) {
    throw createCommandError('validation_failed', 'height must be a positive integer.');
  }

  await chrome.debugger.sendCommand(
    { tabId },
    'Emulation.setDeviceMetricsOverride',
    {
      width,
      height,
      deviceScaleFactor: 1,
      mobile: false
    }
  );
}

async function clearViewportSizeOverride(tabId) {
  await ensureDebuggerSession(tabId);
  await chrome.debugger.sendCommand(
    { tabId },
    'Emulation.clearDeviceMetricsOverride'
  );
}

async function executeInTab(tabId, frameId, func, args = []) {
  const target = frameId != null
    ? { tabId, frameIds: [frameId] }
    : { tabId };

  const results = await chrome.scripting.executeScript({
    target,
    func,
    args
  });

  return results?.[0]?.result;
}

async function focusLocator(tabId, frameId, selector, timeoutMs) {
  await ensureContentScript(tabId, frameId);

  const options = frameId != null ? { frameId } : undefined;
  const response = await chrome.tabs.sendMessage(tabId, {
    action: 'locatorFocus',
    selector,
    timeoutMs: getCommandTimeout({ timeoutMs })
  }, options);

  if (!response?.success) {
    throw createCommandError(
      response?.errorCode ?? 'execution_failed',
      response?.error ?? `Failed to focus selector '${selector}'.`
    );
  }
}

async function pressKeyOnTab(tabId, key) {
  await ensureDebuggerSession(tabId);

  const keyDefinition = createKeyDefinition(key);
  const basePayload = {
    key: keyDefinition.key,
    code: keyDefinition.code,
    windowsVirtualKeyCode: keyDefinition.windowsVirtualKeyCode,
    nativeVirtualKeyCode: keyDefinition.nativeVirtualKeyCode
  };

  const keyDownPayload = {
    ...basePayload,
    type: keyDefinition.keyDownType
  };

  if (keyDefinition.text) {
    keyDownPayload.text = keyDefinition.text;
    keyDownPayload.unmodifiedText = keyDefinition.unmodifiedText ?? keyDefinition.text;
  }

  await chrome.debugger.sendCommand({ tabId }, 'Input.dispatchKeyEvent', keyDownPayload);

  if (keyDefinition.dispatchCharEvent && keyDefinition.text) {
    await chrome.debugger.sendCommand(
      { tabId },
      'Input.dispatchKeyEvent',
      {
        ...basePayload,
        type: 'char',
        text: keyDefinition.text,
        unmodifiedText: keyDefinition.unmodifiedText ?? keyDefinition.text
      }
    );
  }

  await chrome.debugger.sendCommand(
    { tabId },
    'Input.dispatchKeyEvent',
    {
      ...basePayload,
      type: 'keyUp'
    }
  );
}

async function dragLocatorTo(tabId, frameId, sourceSelector, targetSelector, button, moveSteps, timeoutMs) {
  if (frameId != null) {
    throw createCommandError(
      'unsupported_action',
      'locatorDragTo currently supports only the top-level document.'
    );
  }

  await ensureDebuggerSession(tabId);
  await chrome.tabs.update(tabId, { active: true });
  await chrome.debugger.sendCommand({ tabId }, 'Page.bringToFront');

  if (typeof sourceSelector !== 'string' || sourceSelector.trim().length === 0) {
    throw createCommandError('validation_failed', 'sourceSelector is required.');
  }

  if (typeof targetSelector !== 'string' || targetSelector.trim().length === 0) {
    throw createCommandError('validation_failed', 'targetSelector is required.');
  }

  const effectiveTimeout = getCommandTimeout({ timeoutMs });
  const startedAt = Date.now();
  const normalizedMoveSteps = getCommandMoveSteps({ moveSteps });
  const mouseButton = normalizeMouseButton(button);
  const sourcePoint = await resolveLocatorPoint(
    tabId,
    sourceSelector,
    getRemainingTimeout(startedAt, effectiveTimeout));
  const targetPoint = await resolveLocatorPoint(
    tabId,
    targetSelector,
    getRemainingTimeout(startedAt, effectiveTimeout));
  let currentPoint = sourcePoint;
  let mousePressed = false;

  try {
    await dispatchMouseEvent(tabId, {
      type: 'mouseMoved',
      x: sourcePoint.x,
      y: sourcePoint.y,
      button: 'none',
      buttons: 0,
      clickCount: 0
    });

    ensureTimeoutRemaining(startedAt, effectiveTimeout);

    await dispatchMouseEvent(tabId, {
      type: 'mousePressed',
      x: sourcePoint.x,
      y: sourcePoint.y,
      button: mouseButton.button,
      buttons: mouseButton.buttons,
      clickCount: 1
    });
    mousePressed = true;

    for (let stepIndex = 1; stepIndex <= normalizedMoveSteps; stepIndex += 1) {
      ensureTimeoutRemaining(startedAt, effectiveTimeout);

      const progress = stepIndex / normalizedMoveSteps;
      currentPoint = {
        x: interpolate(sourcePoint.x, targetPoint.x, progress),
        y: interpolate(sourcePoint.y, targetPoint.y, progress)
      };

      await delay(16);
      ensureTimeoutRemaining(startedAt, effectiveTimeout);

      await dispatchMouseEvent(tabId, {
        type: 'mouseMoved',
        x: currentPoint.x,
        y: currentPoint.y,
        button: mouseButton.button,
        buttons: mouseButton.buttons,
        clickCount: 1
      });
    }

    ensureTimeoutRemaining(startedAt, effectiveTimeout);

    await dispatchMouseEvent(tabId, {
      type: 'mouseReleased',
      x: targetPoint.x,
      y: targetPoint.y,
      button: mouseButton.button,
      buttons: 0,
      clickCount: 1
    });
    mousePressed = false;
  } finally {
    if (mousePressed) {
      try {
        await dispatchMouseEvent(tabId, {
          type: 'mouseReleased',
          x: currentPoint.x,
          y: currentPoint.y,
          button: mouseButton.button,
          buttons: 0,
          clickCount: 1
        });
      } catch {
      }
    }
  }
}

async function resolveLocatorPoint(tabId, selector, timeoutMs) {
  const snapshot = await executeInTab(tabId, null, readLocatorPointSnapshot, [selector]);
  if (!snapshot?.exists) {
    throw createCommandError('element_not_found', `Element '${selector}' was not found.`);
  }

  if (!snapshot?.visible || !Number.isFinite(snapshot.x) || !Number.isFinite(snapshot.y)) {
    throw createCommandError('element_not_visible', `Element '${selector}' is not visible.`);
  }

  return {
    x: snapshot.x,
    y: snapshot.y
  };
}

async function dispatchMouseEvent(tabId, payload) {
  await chrome.debugger.sendCommand(
    { tabId },
    'Input.dispatchMouseEvent',
    {
      ...payload,
      x: payload.x,
      y: payload.y,
      pointerType: 'mouse'
    }
  );
}

function ensureTimeoutRemaining(startedAt, timeoutMs) {
  if (Date.now() - startedAt > timeoutMs) {
    throw createCommandError('timeout', `Timed out after ${timeoutMs} ms.`);
  }
}

function getRemainingTimeout(startedAt, timeoutMs) {
  ensureTimeoutRemaining(startedAt, timeoutMs);
  return Math.max(1, timeoutMs - (Date.now() - startedAt));
}

function interpolate(start, end, progress) {
  return start + ((end - start) * progress);
}

function createKeyDefinition(key) {
  const normalizedKey = String(key ?? '').trim();
  if (!normalizedKey) {
    throw createCommandError('validation_failed', 'key is required.');
  }

  const namedKeys = {
    Enter: { key: 'Enter', code: 'Enter', windowsVirtualKeyCode: 13, nativeVirtualKeyCode: 13, keyDownType: 'keyDown', dispatchCharEvent: false },
    Tab: { key: 'Tab', code: 'Tab', windowsVirtualKeyCode: 9, nativeVirtualKeyCode: 9, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    Escape: { key: 'Escape', code: 'Escape', windowsVirtualKeyCode: 27, nativeVirtualKeyCode: 27, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    Backspace: { key: 'Backspace', code: 'Backspace', windowsVirtualKeyCode: 8, nativeVirtualKeyCode: 8, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    Delete: { key: 'Delete', code: 'Delete', windowsVirtualKeyCode: 46, nativeVirtualKeyCode: 46, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    ArrowUp: { key: 'ArrowUp', code: 'ArrowUp', windowsVirtualKeyCode: 38, nativeVirtualKeyCode: 38, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    ArrowDown: { key: 'ArrowDown', code: 'ArrowDown', windowsVirtualKeyCode: 40, nativeVirtualKeyCode: 40, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    ArrowLeft: { key: 'ArrowLeft', code: 'ArrowLeft', windowsVirtualKeyCode: 37, nativeVirtualKeyCode: 37, keyDownType: 'rawKeyDown', dispatchCharEvent: false },
    ArrowRight: { key: 'ArrowRight', code: 'ArrowRight', windowsVirtualKeyCode: 39, nativeVirtualKeyCode: 39, keyDownType: 'rawKeyDown', dispatchCharEvent: false }
  };

  if (Object.prototype.hasOwnProperty.call(namedKeys, normalizedKey)) {
    return namedKeys[normalizedKey];
  }

  if (normalizedKey.length === 1) {
    const singleCharacter = normalizedKey;
    if (singleCharacter === ' ') {
      return {
        key: ' ',
        code: 'Space',
        windowsVirtualKeyCode: 32,
        nativeVirtualKeyCode: 32,
        keyDownType: 'keyDown',
        dispatchCharEvent: true,
        text: ' ',
        unmodifiedText: ' '
      };
    }

    const uppercase = singleCharacter.toUpperCase();
    const keyCode = uppercase.charCodeAt(0);
    const isLetter = uppercase >= 'A' && uppercase <= 'Z';
    const isDigit = singleCharacter >= '0' && singleCharacter <= '9';

    if (isLetter || isDigit) {
      return {
        key: singleCharacter,
        code: isLetter ? `Key${uppercase}` : `Digit${singleCharacter}`,
        windowsVirtualKeyCode: keyCode,
        nativeVirtualKeyCode: keyCode,
        keyDownType: 'keyDown',
        dispatchCharEvent: true,
        text: singleCharacter,
        unmodifiedText: singleCharacter
      };
    }
  }

  throw createCommandError(
    'unsupported_action',
    `Unsupported key '${normalizedKey}'. Supported keys currently include Enter, Tab, Escape, Backspace, Delete, Arrow keys, letters, digits, and Space.`
  );
}

function handleDebuggerEvent(session, method, params) {
  switch (method) {
    case 'Runtime.consoleAPICalled':
      appendBufferedEntry(session.consoleMessages, {
        source: 'runtime',
        level: normalizeConsoleLevel(params?.type),
        type: params?.type ?? null,
        text: formatRemoteArguments(params?.args),
        url: params?.stackTrace?.callFrames?.[0]?.url ?? null,
        timestamp: params?.timestamp ?? Date.now()
      });
      break;
    case 'Runtime.exceptionThrown':
      appendBufferedEntry(session.consoleMessages, {
        source: 'exception',
        level: 'error',
        type: 'exception',
        text: params?.exceptionDetails?.exception?.description
          ?? params?.exceptionDetails?.text
          ?? 'Unhandled exception',
        url: params?.exceptionDetails?.url ?? null,
        timestamp: params?.timestamp ?? Date.now()
      });
      break;
    case 'Log.entryAdded':
      appendBufferedEntry(session.consoleMessages, {
        source: params?.entry?.source ?? 'log',
        level: params?.entry?.level ?? 'info',
        type: 'log',
        text: params?.entry?.text ?? '',
        url: params?.entry?.url ?? null,
        timestamp: params?.entry?.timestamp ?? Date.now()
      });
      break;
    case 'Network.requestWillBeSent': {
      const request = getOrCreateNetworkRequest(session, params?.requestId);
      request.url = params?.request?.url ?? request.url ?? null;
      request.method = params?.request?.method ?? request.method ?? null;
      request.resourceType = params?.type ?? request.resourceType ?? null;
      request.startedAt = params?.timestamp ?? request.startedAt ?? Date.now();
      request.failed = false;
      request.errorText = null;
      syncNetworkRequestIndex(session);
      break;
    }
    case 'Network.responseReceived': {
      const request = getOrCreateNetworkRequest(session, params?.requestId);
      request.status = params?.response?.status ?? request.status ?? null;
      request.statusText = params?.response?.statusText ?? request.statusText ?? null;
      request.mimeType = params?.response?.mimeType ?? request.mimeType ?? null;
      request.resourceType = params?.type ?? request.resourceType ?? null;
      request.responseAt = params?.timestamp ?? request.responseAt ?? Date.now();
      syncNetworkRequestIndex(session);
      break;
    }
    case 'Network.loadingFinished': {
      const request = getOrCreateNetworkRequest(session, params?.requestId);
      request.finishedAt = params?.timestamp ?? request.finishedAt ?? Date.now();
      syncNetworkRequestIndex(session);
      break;
    }
    case 'Network.loadingFailed': {
      const request = getOrCreateNetworkRequest(session, params?.requestId);
      request.failed = true;
      request.errorText = params?.errorText ?? request.errorText ?? null;
      request.finishedAt = params?.timestamp ?? request.finishedAt ?? Date.now();
      syncNetworkRequestIndex(session);
      break;
    }
  }
}

function getOrCreateNetworkRequest(session, requestId) {
  const normalizedRequestId = requestId ?? `${session.tabId}-${Date.now()}`;
  let request = session.networkRequestsById.get(normalizedRequestId);
  if (request) {
    return request;
  }

  request = {
    requestId: normalizedRequestId,
    url: null,
    method: null,
    resourceType: null,
    status: null,
    statusText: null,
    mimeType: null,
    failed: false,
    errorText: null,
    startedAt: null,
    responseAt: null,
    finishedAt: null
  };

  session.networkRequestsById.set(normalizedRequestId, request);
  appendBufferedEntry(session.networkRequests, request);
  return request;
}

function appendBufferedEntry(buffer, entry) {
  buffer.push(entry);
  while (buffer.length > DEBUGGER_BUFFER_LIMIT) {
    buffer.shift();
  }
}

function syncNetworkRequestIndex(session) {
  if (session.networkRequestsById.size <= DEBUGGER_BUFFER_LIMIT * 2) {
    return;
  }

  const activeRequestIds = new Set(
    session.networkRequests
      .map(request => request?.requestId)
      .filter(requestId => typeof requestId === 'string' && requestId.length > 0)
  );

  for (const requestId of session.networkRequestsById.keys()) {
    if (!activeRequestIds.has(requestId)) {
      session.networkRequestsById.delete(requestId);
    }
  }
}

function getBufferedEntries(buffer, limit, clearBuffer) {
  const effectiveLimit = limit > 0 ? limit : 100;
  const snapshot = buffer.slice(-effectiveLimit).map(entry => ({ ...entry }));
  if (clearBuffer) {
    buffer.length = 0;
  }

  return snapshot;
}

function normalizeConsoleLevel(type) {
  switch (type) {
    case 'error':
    case 'assert':
      return 'error';
    case 'warning':
      return 'warning';
    case 'debug':
    case 'trace':
      return 'debug';
    default:
      return 'info';
  }
}

function formatRemoteArguments(args) {
  if (!Array.isArray(args) || args.length === 0) {
    return '';
  }

  return args.map(formatRemoteArgument).join(' ');
}

function formatRemoteArgument(arg) {
  if (Object.prototype.hasOwnProperty.call(arg ?? {}, 'value')) {
    return stringifyValue(arg.value);
  }

  if (typeof arg?.unserializableValue === 'string') {
    return arg.unserializableValue;
  }

  if (typeof arg?.description === 'string') {
    return arg.description;
  }

  return '';
}

function normalizeEvaluationResult(remoteObject) {
  if (!remoteObject) {
    return null;
  }

  if (Object.prototype.hasOwnProperty.call(remoteObject, 'value')) {
    return remoteObject.value;
  }

  if (typeof remoteObject.unserializableValue === 'string') {
    return remoteObject.unserializableValue;
  }

  return remoteObject.description ?? null;
}

function stringifyValue(value) {
  if (typeof value === 'string') {
    return value;
  }

  try {
    return JSON.stringify(value);
  } catch {
    return String(value);
  }
}

async function waitForLoadState(tabId, frameId, waitState, timeoutMs) {
  const targetState = normalizeLoadState(waitState);
  return waitFor(
    async () => {
      try {
        const readyState = await executeInTab(tabId, frameId, readDocumentReadyState);
        return isReadyStateSatisfied(readyState, targetState)
          ? readyState
          : null;
      } catch {
        return null;
      }
    },
    getCommandTimeout({ timeoutMs }),
    `Timed out after ${getCommandTimeout({ timeoutMs })} ms waiting for load state '${targetState}'.`
  );
}

async function waitForUrl(tabId, expectedUrl, matchMode, timeoutMs) {
  const normalizedMatchMode = normalizeMatchMode(matchMode);
  return waitFor(
    async () => {
      try {
        const tab = await chrome.tabs.get(tabId);
        const currentUrl = tab?.url ?? '';
        return urlMatches(currentUrl, expectedUrl, normalizedMatchMode)
          ? currentUrl
          : null;
      } catch {
        return null;
      }
    },
    getCommandTimeout({ timeoutMs }),
    `Timed out after ${getCommandTimeout({ timeoutMs })} ms waiting for URL '${expectedUrl}'.`
  );
}

async function waitFor(predicate, timeoutMs, timeoutMessage) {
  const startedAt = Date.now();
  const effectiveTimeout = timeoutMs > 0
    ? timeoutMs
    : state.commandTimeout ?? state.defaultCommandTimeout ?? PACKAGED_DEFAULT_COMMAND_TIMEOUT_MS;

  while (Date.now() - startedAt <= effectiveTimeout) {
    const result = await predicate();
    if (result !== null && result !== undefined) {
      return result;
    }

    await delay(100);
  }

  throw createCommandError('timeout', timeoutMessage);
}

function normalizeLoadState(waitState) {
  const normalized = String(waitState ?? 'load').trim().toLowerCase();
  if (normalized === 'load' || normalized === 'domcontentloaded') {
    return normalized;
  }

  throw createCommandError(
    'unsupported_wait_state',
    `Unsupported waitState '${waitState}'. Supported values: load, domcontentloaded.`
  );
}

function isReadyStateSatisfied(readyState, waitState) {
  if (waitState === 'domcontentloaded') {
    return readyState === 'interactive' || readyState === 'complete';
  }

  return readyState === 'complete';
}

function normalizeMatchMode(matchMode) {
  const normalized = String(matchMode ?? 'glob').trim().toLowerCase();
  if (new Set(['exact', 'contains', 'glob', 'regex']).has(normalized)) {
    return normalized;
  }

  throw createCommandError(
    'unsupported_match_mode',
    `Unsupported matchMode '${matchMode}'. Supported values: exact, contains, glob, regex.`
  );
}

function urlMatches(actualUrl, expectedUrl, matchMode) {
  if (typeof actualUrl !== 'string' || typeof expectedUrl !== 'string') {
    return false;
  }

  switch (matchMode) {
    case 'exact':
      return actualUrl === expectedUrl;
    case 'contains':
      return actualUrl.includes(expectedUrl);
    case 'regex':
      return new RegExp(expectedUrl).test(actualUrl);
    case 'glob':
      return globToRegExp(expectedUrl).test(actualUrl);
    default:
      return false;
  }
}

function globToRegExp(pattern) {
  const escaped = String(pattern)
    .replace(/[.+^${}()|[\]\\]/g, '\\$&')
    .replace(/\*\*/g, '::double-star::')
    .replace(/\*/g, '[^/]*')
    .replace(/::double-star::/g, '.*');

  return new RegExp(`^${escaped}$`);
}

function getCommandTimeout(command) {
  return command?.timeoutMs > 0
    ? command.timeoutMs
    : state.commandTimeout ?? state.defaultCommandTimeout ?? PACKAGED_DEFAULT_COMMAND_TIMEOUT_MS;
}

function getCommandLimit(command) {
  return command?.limit > 0
    ? command.limit
    : 100;
}

function normalizeScreenshotFormat(format) {
  const normalized = String(format ?? 'png').trim().toLowerCase();
  if (new Set(['png', 'jpeg', 'webp']).has(normalized)) {
    return normalized;
  }

  throw createCommandError(
    'validation_failed',
    `Unsupported screenshot format '${format}'. Supported values: png, jpeg, webp.`
  );
}

function createCommandError(errorCode, message) {
  const error = new Error(message);
  error.browserCommanderCode = errorCode;
  return error;
}

function getErrorCode(error) {
  return error?.browserCommanderCode ?? 'execution_failed';
}

function getErrorMessage(error) {
  if (typeof error?.message === 'string' && error.message.length > 0) {
    return error.message;
  }

  return String(error);
}

function delay(timeoutMs) {
  return new Promise(resolve => setTimeout(resolve, timeoutMs));
}

function readDocumentReadyState() {
  return document.readyState;
}

function readDocumentContent() {
  return document.documentElement?.outerHTML ?? '';
}

function readLocatorPointSnapshot(selector) {
  const element = document.querySelector(selector);
  if (!(element instanceof Element)) {
    return {
      exists: false,
      visible: false,
      x: null,
      y: null
    };
  }

  element.scrollIntoView({
    block: 'center',
    inline: 'center',
    behavior: 'instant'
  });

  const style = getComputedStyle(element);
  const rect = element.getBoundingClientRect();
  const visible = style.display !== 'none'
    && style.visibility !== 'hidden'
    && style.visibility !== 'collapse'
    && style.opacity !== '0'
    && rect.width > 0
    && rect.height > 0
    && element.getClientRects().length > 0;

  return {
    exists: true,
    visible,
    x: visible ? rect.left + (rect.width / 2) : null,
    y: visible ? rect.top + (rect.height / 2) : null
  };
}

async function sendInvocation(target, args, connection = state.currentConnection) {
  if (!connection || state.currentConnection !== connection || connection.socket.readyState !== WebSocket.OPEN)
    throw createError('agent_disconnected', 'SignalR socket is not open.');
  const invocationId = String(++connection.invocationId);
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      connection.pending.delete(invocationId);
      reject(createError('sync_failed', `SignalR ${target} timed out.`));
    }, 12000);
    connection.pending.set(invocationId, { resolve, reject, timeout });
    try {
      connection.socket.send(JSON.stringify({
        type: 1, invocationId, target, arguments: args
      }) + RECORD_SEPARATOR);
    } catch (error) {
      connection.pending.delete(invocationId);
      clearTimeout(timeout);
      reject(error);
    }
  });
}

async function sendCommandCompletion(result, command, connection = state.currentConnection) {
  // Results from a displaced transport cannot complete commands of the new generation.
  if (connection !== state.currentConnection) return;
  await sendInvocation('CompleteCommand', [result], connection);
}

async function flushPendingCommandResults() {
  // Do not replay results originating from obsolete connections.
}
async function getAuthorizedTabsSnapshot() {
  const tabs = await getTabsByIds(state.authorizedTabIds);
  const windowById = new Map(state.authorizedTabRecords.map(record => [record.tabId, record.windowId]));
  return tabs.filter(tab => windowById.get(tab.id) === tab.windowId
    && !state.blockedTabIds.has(tab.id)).map(mapTabDescriptor);
}

async function getAuthorizedTabsSnapshotSafe() {
  try {
    return await getAuthorizedTabsSnapshot();
  } catch {
    return [];
  }
}

async function getTabsByIds(tabIds) {
  const ids = [...new Set((tabIds ?? []).filter(Number.isInteger))];
  if (ids.length === 0) {
    return [];
  }

  const tabs = await Promise.all(ids.map(async tabId => {
    try {
      return await chrome.tabs.get(tabId);
    } catch {
      return null;
    }
  }));

  return tabs.filter(tab => tab && typeof tab.id === 'number');
}

function mapTabDescriptor(tab) {
  return {
    tabId: tab.id,
    windowId: tab.windowId,
    active: Boolean(tab.active),
    url: tab.url ?? null,
    title: tab.title ?? null
  };
}

async function detectBrowserName() {
  const brandNames = Array.isArray(navigator.userAgentData?.brands)
    ? navigator.userAgentData.brands
      .map(brand => brand?.brand?.trim())
      .filter(Boolean)
    : [];

  if (brandNames.some(brand => brand.includes('Microsoft Edge'))) {
    return 'Microsoft Edge';
  }

  if (brandNames.some(brand => brand.includes('Opera'))) {
    return 'Opera';
  }

  if (brandNames.some(brand => brand.includes('Vivaldi'))) {
    return 'Vivaldi';
  }

  if (typeof navigator.brave?.isBrave === 'function') {
    try {
      if (await navigator.brave.isBrave()) {
        return 'Brave';
      }
    } catch {
    }
  }

  const userAgent = navigator.userAgent ?? '';
  if (userAgent.includes('Edg/')) {
    return 'Microsoft Edge';
  }

  if (userAgent.includes('OPR/')) {
    return 'Opera';
  }

  if (userAgent.includes('Vivaldi/')) {
    return 'Vivaldi';
  }

  if (userAgent.includes('Chrome/')) {
    return 'Google Chrome';
  }

  return 'Chromium';
}

// Authorization is owned exclusively by chrome.storage.session.

async function syncAuthorizedTabIndicators() {
  const authorizedTabIds = normalizeTabIds(state.authorizedTabIds);
  const previousOverrideTabIds = normalizeTabIds(state.tabIndicatorOverrideTabIds);
  const authorizedTabIdSet = new Set(authorizedTabIds);
  const count = authorizedTabIds.length;
  const globalBadgeText = count > 0 ? count.toString() : '';

  try {
    await chrome.action.setBadgeText({ text: globalBadgeText });
    if (count > 0) {
      await chrome.action.setBadgeBackgroundColor({ color: ACTION_AUTHORIZED_BADGE_BACKGROUND_COLOR });
    }

    await Promise.allSettled(
      previousOverrideTabIds
        .filter(tabId => !authorizedTabIdSet.has(tabId))
        .map(tabId => restoreDefaultTabIndicator(tabId, globalBadgeText))
    );

    await Promise.allSettled(
      authorizedTabIds.map(tabId => showAuthorizedTabIndicator(tabId))
    );
  } catch {
    // Badge API may not be available in all environments
  }

  state.tabIndicatorOverrideTabIds = normalizeTabIds([
    ...previousOverrideTabIds,
    ...authorizedTabIds
  ]);
}

async function showAuthorizedTabIndicator(tabId) {
  await chrome.action.setBadgeText({
    tabId,
    text: ACTION_AUTHORIZED_BADGE_TEXT
  });
  await chrome.action.setBadgeBackgroundColor({
    tabId,
    color: ACTION_AUTHORIZED_BADGE_BACKGROUND_COLOR
  });
  await chrome.action.setTitle({
    tabId,
    title: ACTION_AUTHORIZED_TITLE
  });
}

async function restoreDefaultTabIndicator(tabId, globalBadgeText) {
  await chrome.action.setBadgeText({
    tabId,
    text: globalBadgeText
  });
  await chrome.action.setTitle({
    tabId,
    title: ACTION_DEFAULT_TITLE
  });
}

function forgetTabIndicatorOverride(tabId) {
  if (!Number.isInteger(tabId)) {
    return;
  }

  state.tabIndicatorOverrideTabIds = state.tabIndicatorOverrideTabIds.filter(candidate => candidate !== tabId);
}

async function getGlobalPages() {
  try {
    const serverAddress = await getServerAddress();
    const response = await fetch(`${serverAddress}/api/browser-automation/pages`);
    if (!response.ok) {
      return { ok: false, error: `Server returned HTTP ${response.status}.`, pages: [] };
    }
    const pages = await response.json();
    return { ok: true, error: null, pages };
  } catch (error) {
    return { ok: false, error: String(error), pages: [] };
  }
}

async function authorizeTab(tabId) {
  ensureValidTabId(tabId);
  await enqueueMutation(async records => {
    const current = await chrome.tabs.get(tabId).catch(() => null);
    if (!current) throw createError('tab_closed', 'The tab no longer exists.');
    const tabs = await getTabsByIds(records.map(record => record.tabId));
    const byId = new Map(tabs.map(tab => [tab.id, tab]));
    const valid = records.filter(record =>
      byId.get(record.tabId)?.windowId === record.windowId);
    if (valid.some(record => record.tabId === tabId)) {
      state.blockedTabIds.delete(tabId);
      return valid.length === records.length ? null : valid;
    }
    if (valid.length >= MAX_AUTHORIZED_TABS)
      throw createError('authorization_limit_reached', 'Cannot authorize more than 64 tabs in this browser.');
    valid.push({ tabId, windowId: current.windowId });
    return valid;
  });
  state.blockedTabIds.delete(tabId);
  void tryEnsureDebuggerSession(tabId);
  void publishTabs();
}

async function revokeTab(tabId) {
  ensureValidTabId(tabId);
  state.blockedTabIds.add(tabId);
  try {
    await enqueueMutation(records => {
      const valid = records.filter(record => record.tabId !== tabId);
      return valid.length === records.length ? null : valid;
    });
    state.blockedTabIds.delete(tabId);
  } catch (error) {
    scheduleStorageRetry();
    throw createError('storage_failed', `Revocation not yet persisted: ${error}`);
  } finally {
    void detachDebuggerSession(tabId);
  }
}

async function clearAuthorizedTabs() {
  const ids = [...state.authorizedTabIds];
  ids.forEach(id => state.blockedTabIds.add(id));
  try {
    await enqueueMutation(records => records.length ? [] : null);
    ids.forEach(id => state.blockedTabIds.delete(id));
  } catch (error) {
    scheduleStorageRetry();
    throw createError('storage_failed', `Clear not yet persisted: ${error}`);
  } finally {
    await Promise.allSettled(ids.map(id => detachDebuggerSession(id)));
  }
}

async function pruneAuthorizedTabs() {
  await enqueueMutation(async records => {
    const tabs = await getTabsByIds(records.map(record => record.tabId));
    const byId = new Map(tabs.map(tab => [tab.id, tab]));
    const valid = records.filter(record =>
      byId.get(record.tabId)?.windowId === record.windowId
      && !state.blockedTabIds.has(record.tabId));
    return valid.length === records.length ? null : valid;
  });
}

async function removeMissingAuthorizedTabs(missingTabIds) {
  const missing = new Set(normalizeTabIds(missingTabIds));
  if (!missing.size) return;
  try {
    await enqueueMutation(records => {
      const valid = records.filter(record => !missing.has(record.tabId));
      return valid.length === records.length ? null : valid;
    });
    for (const id of missing) {
      state.blockedTabIds.delete(id);
      void detachDebuggerSession(id);
    }
  } catch (error) {
    scheduleStorageRetry();
    throw error;
  }
}

function scheduleStorageRetry() {
  chrome.alarms.create(STORAGE_RETRY_ALARM, { delayInMinutes: 0.5 });
}
async function retryFailedRevocations() {
  if (!state.blockedTabIds.size) return;
  try {
    const blocked = new Set(state.blockedTabIds);
    await enqueueMutation(records => records.filter(record => !blocked.has(record.tabId)));
    for (const id of blocked) state.blockedTabIds.delete(id);
  } catch (error) {
    recordError(createError('storage_failed', String(error)));
    scheduleStorageRetry();
  }
}

async function createStatusResponse(fallback = false) {
  return {
    ok: state.initialized && !state.initializeError,
    agentId: state.agentId ?? '',
    connected: state.connected,
    allowedTabs: fallback || !state.initialized ? [] : await getAuthorizedTabsSnapshot(),
    localAuthorization: state.initialized ? 'Authorized locally' : 'Storage error',
    connectionStatus: state.connectionStatus,
    syncStatus: state.syncStatus,
    stateRevision: state.stateRevision,
    lastAcknowledgedRevision: state.lastAcknowledgedRevision,
    serverInstanceId: state.serverInstanceId,
    lastSyncAt: state.lastSyncAt,
    authorizedTabCount: state.authorizedTabIds.length,
    authorizationLimit: MAX_AUTHORIZED_TABS,
    errorCode: state.lastErrorCode,
    error: state.lastError ?? ''
  };
}

function normalizeTabIds(tabIds) {
  if (!Array.isArray(tabIds)) {
    return [];
  }

  return [...new Set(
    tabIds
      .map(value => Number(value))
      .filter(Number.isInteger)
      .filter(value => value > 0)
  )].sort((left, right) => left - right);
}

function areSameIds(left, right) {
  const normalizedLeft = normalizeTabIds(left);
  const normalizedRight = normalizeTabIds(right);
  if (normalizedLeft.length !== normalizedRight.length) {
    return false;
  }

  return normalizedLeft.every((value, index) => value === normalizedRight[index]);
}

function ensureValidTabId(tabId) {
  if (!Number.isInteger(tabId) || tabId <= 0) {
    throw new Error('A valid tabId is required.');
  }
}

function isTabAuthorized(tabId) {
  return state.initialized && !state.initializeError && !state.blockedTabIds.has(tabId)
    && state.authorizedTabIds.includes(tabId);
}

async function getServerAddress() {
  if (state.serverAddress) {
    return state.serverAddress;
  }

  const stored = await chrome.storage.local.get(SERVER_ADDRESS_KEY);
  const storedServerAddress = normalizeServerAddressOrNull(stored?.[SERVER_ADDRESS_KEY]);
  if (storedServerAddress) {
    state.serverAddress = storedServerAddress;
    return state.serverAddress;
  }

  const defaultServerAddress = await getDefaultServerAddress();
  state.serverAddress = defaultServerAddress;
  return state.serverAddress;
}

async function getDefaultServerAddress() {
  return (await getDefaultExtensionSettings()).defaultServerAddress;
}

async function getConfiguredCommandTimeout() {
  if (Number.isInteger(state.commandTimeout) && state.commandTimeout > 0) {
    return state.commandTimeout;
  }

  const stored = await chrome.storage.local.get(COMMAND_TIMEOUT_KEY);
  const storedCommandTimeout = normalizeCommandTimeoutOrNull(stored?.[COMMAND_TIMEOUT_KEY]);
  if (storedCommandTimeout) {
    state.commandTimeout = storedCommandTimeout;
    return state.commandTimeout;
  }

  const defaultCommandTimeout = await getDefaultCommandTimeout();
  state.commandTimeout = defaultCommandTimeout;
  return state.commandTimeout;
}

async function getDefaultCommandTimeout() {
  return (await getDefaultExtensionSettings()).defaultCommandTimeout;
}

async function getDefaultExtensionSettings() {
  if (state.defaultServerAddress && state.defaultCommandTimeout) {
    return {
      defaultServerAddress: state.defaultServerAddress,
      defaultCommandTimeout: state.defaultCommandTimeout
    };
  }

  const response = await fetch(chrome.runtime.getURL('appsettings.json'));
  if (!response.ok) {
    throw new Error(`Failed to read appsettings.json. HTTP ${response.status}.`);
  }

  const config = await response.json();
  state.defaultServerAddress = normalizeLocalServerAddress(config?.ServerSettings?.ServerAddress ?? '');
  state.defaultCommandTimeout = normalizeCommandTimeoutOrDefault(
    config?.ServerSettings?.DefaultCommandTimeoutMs,
    PACKAGED_DEFAULT_COMMAND_TIMEOUT_MS);

  if (!state.defaultServerAddress) {
    throw new Error('ServerSettings:ServerAddress is missing.');
  }

  return {
    defaultServerAddress: state.defaultServerAddress,
    defaultCommandTimeout: state.defaultCommandTimeout
  };
}

async function getExtensionSettings() {
  return {
    serverAddress: await getServerAddress(),
    defaultServerAddress: await getDefaultServerAddress(),
    commandTimeoutMs: await getConfiguredCommandTimeout(),
    defaultCommandTimeoutMs: await getDefaultCommandTimeout()
  };
}

async function saveExtensionSettings(settings) {
  const previousServerAddress = await getServerAddress();
  const previousCommandTimeout = await getConfiguredCommandTimeout();
  const normalizedServerAddress = normalizeLocalServerAddress(settings?.serverAddress);
  const normalizedCommandTimeout = normalizeCommandTimeout(settings?.commandTimeoutMs);

  await chrome.storage.local.set({
    [SERVER_ADDRESS_KEY]: normalizedServerAddress,
    [COMMAND_TIMEOUT_KEY]: normalizedCommandTimeout
  });

  state.serverAddress = normalizedServerAddress;
  state.commandTimeout = normalizedCommandTimeout;

  if (normalizedServerAddress !== previousServerAddress) {
    await reconnectToConfiguredServer();
    return;
  }

  if (normalizedCommandTimeout !== previousCommandTimeout && state.connected) {
    await reconnectToConfiguredServer();
  }
}

async function reconnectToConfiguredServer() {
  clearReconnectTimer();
  ++state.connectionGeneration;
  const connection = state.currentConnection;
  state.connectPromise = null;
  if (connection) {
    closeConnection(connection);
    connection.socket.close(1000, 'Server address changed.');
  }
  await ensureConnected();
}

function normalizeLocalServerAddress(value) {
  const normalized = normalizeServerAddressOrNull(value);
  if (!normalized) {
    throw new Error('Server address is required.');
  }

  const url = new URL(normalized);
  if (!isLoopbackHostname(url.hostname)) {
    throw new Error('Only localhost or loopback server addresses are supported in this build.');
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new Error('Server address must use http or https.');
  }

  return url.origin;
}

function normalizeCommandTimeout(value) {
  const normalized = normalizeCommandTimeoutOrNull(value);
  if (!normalized) {
    throw new Error('Command timeout must be a positive integer in milliseconds.');
  }

  return normalized;
}

function normalizeCommandTimeoutOrDefault(value, fallbackValue) {
  return normalizeCommandTimeoutOrNull(value) ?? fallbackValue;
}

function normalizeCommandTimeoutOrNull(value) {
  const numericValue = typeof value === 'number'
    ? value
    : Number.parseInt(String(value ?? '').trim(), 10);

  return Number.isInteger(numericValue) && numericValue > 0
    ? numericValue
    : null;
}

function normalizeServerAddressOrNull(value) {
  if (typeof value !== 'string' || value.trim().length === 0) {
    return null;
  }

  try {
    const url = new URL(value.trim());
    return url.origin;
  } catch {
    return null;
  }
}

function isLoopbackHostname(hostname) {
  const normalized = String(hostname ?? '').trim().toLowerCase();
  return normalized === 'localhost'
    || normalized === '127.0.0.1'
    || normalized === '[::1]'
    || normalized === '::1';
}

function createWebSocketUrl(serverAddress, connectionToken) {
  const url = new URL(`${serverAddress}/browserCommanderHub`);
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:';
  url.searchParams.set('id', connectionToken);
  return url.toString();
}

function parseHubMessages(rawData, connection) {
  connection.buffer += rawData;
  if (connection.buffer.length > 8 * 1024 * 1024)
    throw createError('protocol_incompatible', 'Oversized SignalR frame.');
  const messages = [];
  let index = connection.buffer.indexOf(RECORD_SEPARATOR);
  while (index >= 0) {
    const frame = connection.buffer.slice(0, index);
    connection.buffer = connection.buffer.slice(index + 1);
    if (!frame) throw createError('protocol_incompatible', 'Empty SignalR frame.');
    messages.push(JSON.parse(frame));
    index = connection.buffer.indexOf(RECORD_SEPARATOR);
  }
  return messages;
}

function createUnexpectedCommandFailureResult(command, error) {
  return {
    ...createBaseResult(command, normalizeAction(command?.action)),
    errorCode: getErrorCode(error),
    error: getErrorMessage(error)
  };
}

function formatCommandLogContext(source) {
  if (!source) {
    return 'commandId=<unknown>, action=<unknown>, tabId=<unknown>';
  }

  return `commandId=${source.commandId ?? '<unknown>'}, action=${source.action ?? '<unknown>'}, tabId=${source.tabId ?? '<unknown>'}`;
}

function normalizeAction(action) {
  switch (action) {
    case 'updateText':
      return 'setText';
    default:
      return action;
  }
}

function startKeepAlive(connection) {
  stopKeepAlive();
  state.keepAliveTimer = setInterval(() => {
    if (state.currentConnection === connection && connection.socket.readyState === WebSocket.OPEN)
      connection.socket.send(JSON.stringify({ type: 6 }) + RECORD_SEPARATOR);
  }, 25000);
}
function stopKeepAlive() {
  if (state.keepAliveTimer) clearInterval(state.keepAliveTimer);
  state.keepAliveTimer = null;
}
function scheduleReconnect() {
  if (state.reconnectTimer) return;
  const delay = Math.min(30000, 500 * Math.pow(2, Math.min(state.reconnectAttempts++, 6)))
    + Math.floor(Math.random() * 250);
  chrome.alarms.create(RECONNECT_ALARM, { delayInMinutes: Math.max(0.5, delay / 60000) });
  state.reconnectTimer = setTimeout(() => {
    state.reconnectTimer = null;
    void ensureConnected().catch(recordError);
  }, delay);
}

function clearReconnectTimer() {
  if (state.reconnectTimer) {
    clearTimeout(state.reconnectTimer);
    state.reconnectTimer = null;
  }
}
