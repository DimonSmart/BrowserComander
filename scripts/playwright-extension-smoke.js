const fs = require('fs');
const os = require('os');
const path = require('path');
const { chromium } = require('playwright');

const extensionPath = path.resolve(__dirname, '..', 'BrowserCommander', 'bin', 'Debug', 'net8.0', 'browserextension');
const smokeUrl = process.env.BROWSER_COMMANDER_SMOKE_URL ?? 'http://127.0.0.1:8765/';
const automationApiBase = process.env.BROWSER_COMMANDER_API_BASE ?? 'http://localhost:5082/api/browser-automation';

function assert(condition, message) {
  if (!condition) {
    throw new Error(message);
  }
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

    const setTextResponse = await fetch(`${automationApiBase}/set-text`, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({
        agentId: agent.agentId,
        tabId: tab.tabId,
        selector: '#target-textarea',
        text: 'Playwright smoke text'
      })
    });

    assert(setTextResponse.ok, `set-text request failed with HTTP ${setTextResponse.status}.`);

    const setTextResult = await setTextResponse.json();
    assert(setTextResult.success, `set-text returned an error: ${setTextResult.error ?? 'unknown error'}.`);

    await page.waitForFunction(
      expected => document.querySelector('#target-textarea')?.value === expected,
      'Playwright smoke text',
      { timeout: 15000 });

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
