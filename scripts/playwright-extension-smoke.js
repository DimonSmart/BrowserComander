const fs = require('fs');
const os = require('os');
const path = require('path');
const { chromium } = require('playwright');

const extensionPath = path.resolve(__dirname, '..', 'BrowserCommander', 'bin', 'Debug', 'net8.0', 'browserextension');
const smokeUrl = process.env.BROWSER_COMMANDER_SMOKE_URL ?? 'http://127.0.0.1:8765/';
const automationApiBase = process.env.BROWSER_COMMANDER_API_BASE ?? 'http://localhost:5082/api/browser-automation';
const iPhone12ProPortrait = { width: 390, height: 844 };

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
}

async function executeCommand(command) {
  const response = await fetch(`${automationApiBase}/commands`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(command)
  });

  const result = await response.json();
  if (!response.ok) {
    throw new Error(
      `Command '${command.action}' failed with HTTP ${response.status}: ${result?.error ?? 'unknown error'}.`);
  }

  if (!result?.success) {
    throw new Error(`Command '${command.action}' returned an error: ${result?.error ?? 'unknown error'}.`);
  }

  return result;
}

async function evaluateValue(agentId, tabId, expression) {
  const result = await executeCommand({
    agentId,
    tabId,
    action: 'pageEvaluate',
    script: expression
  });

  return JSON.parse(result.valueJson);
}

async function getAgents() {
  const response = await fetch(`${automationApiBase}/agents`);
  if (!response.ok) {
    throw new Error(`Failed to read agents. HTTP ${response.status}.`);
  }

  return response.json();
}

async function main() {
  const userDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'browsercommander-playwright-'));
  const context = await chromium.launchPersistentContext(userDataDir, {
    headless: false,
    args: [
      `--disable-extensions-except=${extensionPath}`,
      `--load-extension=${extensionPath}`
    ]
  });

  try {
    let extensionWorker = context.serviceWorkers().find(worker => worker.url().startsWith('chrome-extension://'));
    if (!extensionWorker) {
      extensionWorker = await context.waitForEvent('serviceworker', {
        timeout: 30000,
        predicate: worker => worker.url().startsWith('chrome-extension://')
      });
    }

    const extensionUrl = new URL(extensionWorker.url());
    const extensionId = extensionUrl.host;

    console.log(JSON.stringify({ extensionWorker: extensionWorker.url(), extensionId }, null, 2));

    const page = await context.newPage();
    await page.goto(smokeUrl, { waitUntil: 'networkidle' });

    const extensionPage = await context.newPage();
    const authorization = await extensionPage.goto(`chrome-extension://${extensionId}/index.html`)
      .then(async () => extensionPage.evaluate(async targetSmokeUrl => {
        const tabs = await chrome.tabs.query({});
        const targetTab = tabs.find(tab => tab.url?.startsWith(targetSmokeUrl));
        if (!targetTab?.id) {
          throw new Error('The smoke page tab was not found in chrome.tabs.query().');
        }

        const status = await chrome.runtime.sendMessage({
          type: 'authorizeTab',
          tabId: targetTab.id
        });

        return {
          tabId: targetTab.id,
          status
        };
      }, smokeUrl));

    assert(authorization?.status?.ok, `Failed to authorize smoke tab: ${authorization?.status?.error ?? 'unknown error'}.`);

    let agents = [];
    for (let attempt = 0; attempt < 30; attempt += 1) {
      agents = await getAgents();
      const authorizedSmokeTab = agents
        .flatMap(agent => agent.tabs ?? [])
        .find(candidate => candidate.tabId === authorization.tabId && candidate.url?.startsWith(smokeUrl));

      if (agents.length > 0 && authorizedSmokeTab) {
        break;
      }

      await page.waitForTimeout(1000);
    }

    assert(agents.length > 0, 'The browser agent did not register in the server.');

    const agent = agents[0];
    const tab = (agent.tabs ?? []).find(candidate => candidate.url?.startsWith(smokeUrl));

    assert(Boolean(agent.agentId), 'The registered agent does not expose agentId.');
    assert(Boolean(tab?.tabId), 'The smoke page tab was not published by the extension.');

    const setTextResult = await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'setText',
      selector: '#target-textarea',
      text: 'Playwright smoke text'
    });

    await page.waitForFunction(
      expected => document.querySelector('#target-textarea')?.value === expected,
      'Playwright smoke text',
      { timeout: 15000 });

    const initialViewport = await evaluateValue(
      agent.agentId,
      tab.tabId,
      '({ width: window.innerWidth, height: window.innerHeight })');

    await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'pageSetViewportSize',
      width: iPhone12ProPortrait.width,
      height: iPhone12ProPortrait.height
    });

    await page.waitForFunction(
      expected => window.innerWidth === expected.width && window.innerHeight === expected.height,
      iPhone12ProPortrait,
      { timeout: 15000 });

    const portraitViewport = await evaluateValue(
      agent.agentId,
      tab.tabId,
      '({ width: window.innerWidth, height: window.innerHeight })');

    assert(
      portraitViewport.width === iPhone12ProPortrait.width
      && portraitViewport.height === iPhone12ProPortrait.height,
      `Viewport override did not apply. Actual: ${JSON.stringify(portraitViewport)}.`);

    const landscapeViewport = {
      width: iPhone12ProPortrait.height,
      height: iPhone12ProPortrait.width
    };

    await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'pageSetViewportSize',
      width: landscapeViewport.width,
      height: landscapeViewport.height
    });

    await page.waitForFunction(
      expected => window.innerWidth === expected.width && window.innerHeight === expected.height,
      landscapeViewport,
      { timeout: 15000 });

    await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'pageReload',
      waitState: 'load'
    });

    await page.waitForLoadState('load');

    const reloadedViewport = await evaluateValue(
      agent.agentId,
      tab.tabId,
      '({ width: window.innerWidth, height: window.innerHeight })');

    assert(
      reloadedViewport.width === landscapeViewport.width
      && reloadedViewport.height === landscapeViewport.height,
      `Viewport override did not persist after reload. Actual: ${JSON.stringify(reloadedViewport)}.`);

    await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'pageClearViewportOverride'
    });

    await page.waitForFunction(
      expected => window.innerWidth === expected.width && window.innerHeight === expected.height,
      initialViewport,
      { timeout: 15000 });

    const clearedViewport = await evaluateValue(
      agent.agentId,
      tab.tabId,
      '({ width: window.innerWidth, height: window.innerHeight })');

    assert(
      clearedViewport.width === initialViewport.width
      && clearedViewport.height === initialViewport.height,
      `Viewport override did not clear. Actual: ${JSON.stringify(clearedViewport)}. Expected: ${JSON.stringify(initialViewport)}.`);

    console.log(JSON.stringify({ agentId: agent.agentId, tabId: tab.tabId }, null, 2));
    console.log('Smoke test passed.');
  } finally {
    await context.close();
    fs.rmSync(userDataDir, { recursive: true, force: true });
  }
}

main().catch(error => {
  console.error(error);
  process.exitCode = 1;
});
