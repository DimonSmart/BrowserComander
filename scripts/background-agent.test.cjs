const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const crypto = require('node:crypto');

const source = fs.readFileSync('BrowserCommander/wwwroot/background-agent.js', 'utf8');
const sessionKey = 'browserCommander.authorizationState';
const rs = '\x1e';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function waitFor(check, timeout = 3000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await check()) return;
    await sleep(15);
  }
  assert.fail('Timed out waiting for background agent condition.');
}

function createHarness({ count = 3, online = false, sessionStorage = {} } = {}) {
  const tabs = Array.from({ length: count }, (_, i) => ({
    id: i + 1, windowId: 1, active: i === 0,
    title: 'Tab ' + (i + 1), url: 'https://example.com/' + (i + 1)
  }));
  const listeners = {};
  const saved = { ...sessionStorage };
  const local = {
    'browserCommander.serverAddress': 'http://localhost:5082',
    'browserCommander.commandTimeoutMs': 30000
  };
  const invocations = [];
  const sockets = [];
  let connected = online;
  let failStorageSet = false;

  function event(name) {
    return { addListener(handler) { listeners[name] = handler; } };
  }
  function emit(socket, data) {
    for (const listener of socket.listeners.message ?? [])
      listener({ data: JSON.stringify(data) + rs });
  }
  class MockSocket {
    static OPEN = 1;
    constructor(url) {
      this.readyState = 0;
      this.listeners = {};
      sockets.push(this);
      queueMicrotask(() => {
        this.readyState = 1;
        this.fire('open', {});
        emit(this, {});
      });
    }
    addEventListener(name, listener) {
      (this.listeners[name] ??= []).push(listener);
    }
    fire(name, data) {
      for (const fn of this.listeners[name] ?? []) fn(data);
    }
    send(raw) {
      const request = JSON.parse(raw.split(rs)[0]);
      if (!request.target) return;
      invocations.push(request);
      if (request.invocationId) {
        const revision = request.arguments?.[0]?.stateRevision ?? 0;
        queueMicrotask(() => emit(this, {
          type: 3, invocationId: request.invocationId,
          result: request.target === 'CompleteCommand'
            ? null : { accepted: true, stateRevision: revision, serverInstanceId: 'server-1' }
        }));
      }
    }
    close() {
      this.readyState = 3;
      this.fire('close', { code: 1000 });
    }
  }

  const chrome = {
    runtime: {
      id: 'extension-1', getURL: value => 'chrome-extension://extension-1/' + value,
      onMessage: event('message'), onInstalled: event('installed'), onStartup: event('startup')
    },
    tabs: {
      onActivated: event('activated'), onCreated: event('created'),
      onRemoved: event('removed'), onUpdated: event('updated'),
      query: async () => tabs.slice(),
      get: async id => {
        const tab = tabs.find(t => t.id === id);
        if (!tab) throw Error('No tab');
        return tab;
      },
      create: async () => ({})
    },
    storage: {
      session: {
        get: async key => ({ [key]: saved[key] }),
        set: async values => {
          if (failStorageSet) throw Error('Simulated storage failure');
          Object.assign(saved, structuredClone(values));
        }
      },
      local: {
        get: async key => ({ [key]: local[key] }),
        set: async values => Object.assign(local, values)
      }
    },
    action: {
      setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {},
      setTitle: async () => {}
    },
    debugger: {
      onEvent: event('debugEvent'), onDetach: event('debugDetach'),
      getTargets: async () => [], attach: async () => {},
      sendCommand: async () => ({}), detach: async () => {}
    },
    alarms: { onAlarm: event('alarm'), create: () => {}, clear: async () => {} }
  };
  const context = {
    chrome, WebSocket: MockSocket, navigator: { userAgent: 'Chrome/125' },
    crypto: crypto.webcrypto, console: { ...console, warn: () => {} },
    URL, fetch: async url => {
      if (!connected) throw Error('Offline');
      if (String(url).includes('/negotiate')) {
        return { ok: true, json: async () => ({ connectionToken: 'token' }) };
      }
      return { ok: true, json: async () => [] };
    },
    setTimeout: (fn, ms) => {
      const timer = setTimeout(fn, ms);
      if (ms > 1000) timer.unref();
      return timer;
    },
    clearTimeout, setInterval: () => 0, clearInterval: () => {},
    structuredClone, Date, JSON, Promise
  };
  vm.runInNewContext(source, context, { filename: 'background-agent.js' });
  return {
    saved, tabs, invocations, sockets, listeners,
    setOnline(value) { connected = value; },
    failStorage(value) { failStorageSet = value; },
    async message(type, tabId, additional = {}) {
      return new Promise(resolve => listeners.message({ type, tabId, ...additional }, {}, resolve));
    },
    async status() { return this.message('status'); }
  };
}

test('offline authorization is committed in session storage and survives worker restart', async () => {
  const h = createHarness({ online: false, count: 2 });
  await waitFor(async () => Boolean(h.saved[sessionKey]));
  const response = await h.message('authorizeTab', 1);
  assert.equal(response.ok, true);
  assert.equal(response.connected, false);
  assert.equal(response.syncStatus, 'Pending');
  assert.deepEqual(h.saved[sessionKey].authorizedTabs.map(t => t.tabId), [1]);

  const restarted = createHarness({ online: false, count: 2, sessionStorage: h.saved });
  await waitFor(async () => (await restarted.status()).allowedTabs.length === 1);
  assert.equal((await restarted.status()).agentId, response.agentId);
});

test('parallel authorizations cannot exceed 64 grants per profile', async () => {
  const h = createHarness({ online: false, count: 66 });
  await waitFor(async () => Boolean(h.saved[sessionKey]));
  const results = await Promise.all(h.tabs.map(t => h.message('authorizeTab', t.id)));
  assert.equal(results.filter(r => r.ok).length, 64);
  assert.equal(h.saved[sessionKey].authorizedTabs.length, 64);
  assert.equal(results.filter(r => r.errorCode === 'authorization_limit_reached').length, 2);
  assert.equal((await h.message('authorizeTab', 1)).ok, true);
  assert.equal(h.saved[sessionKey].authorizedTabs.length, 64);
  assert.equal((await h.message('revokeTab', 1)).ok, true);
  assert.equal((await h.message('authorizeTab', 65)).ok, true);
});

test('failed storage write never authorizes and server ACK is required for Synced', async () => {
  const h = createHarness({ online: true, count: 2 });
  await waitFor(async () => (await h.status()).syncStatus === 'Synced');
  h.failStorage(true);
  const failed = await h.message('authorizeTab', 1);
  assert.equal(failed.ok, false);
  assert.equal(h.saved[sessionKey].authorizedTabs.length, 0);
  h.failStorage(false);
  const authorized = await h.message('authorizeTab', 1);
  assert.equal(authorized.ok, true);
  await waitFor(async () => (await h.status()).syncStatus === 'Synced'
    && (await h.status()).lastAcknowledgedRevision >= 1);
  assert.equal(h.invocations.some(i => i.target === 'RegisterAgent' && i.invocationId), true);
  assert.equal(h.invocations.some(i => i.target === 'UpdateTabs' && i.invocationId), true);
});

test('closing a tab removes only its grant and tab ID reuse cannot restore it', async () => {
  const h = createHarness({ online: false, count: 2 });
  await waitFor(async () => Boolean(h.saved[sessionKey]));
  await h.message('authorizeTab', 1);
  h.tabs.shift();
  h.listeners.removed(1);
  await waitFor(async () => h.saved[sessionKey].authorizedTabs.length === 0);
  h.tabs.push({ id: 1, windowId: 2, active: false, title: 'Replacement', url: 'https://new.test' });
  assert.equal((await h.status()).allowedTabs.length, 0);
});

test('late close and message from an obsolete socket cannot reset a new connection', async () => {
  const h = createHarness({ online: true, count: 1 });
  await waitFor(async () => (await h.status()).syncStatus === 'Synced');
  const first = h.sockets[0];
  const switched = await h.message('saveExtensionSettings', null, {
    serverAddress: 'http://localhost:5091', commandTimeoutMs: 30000
  });
  assert.equal(switched.ok, true);
  await waitFor(async () => h.sockets.length >= 2
    && (await h.status()).syncStatus === 'Synced');
  first.fire('close', { code: 1006 });
  for (const listener of first.listeners.message ?? [])
    listener({ data: JSON.stringify({ type: 7 }) + rs });
  const status = await h.status();
  assert.equal(status.connected, true);
  assert.equal(status.syncStatus, 'Synced');
});
