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

async function sendCommand(command) {
  const response = await fetch(`${automationApiBase}/commands`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify(command)
  });

  const result = await response.json();
  return {
    ok: response.ok,
    status: response.status,
    result
  };
}

async function executeCommand(command) {
  const response = await sendCommand(command);
  if (!response.ok) {
    throw new Error(
      `Command '${command.action}' failed with HTTP ${response.status}: ${response.result?.error ?? 'unknown error'}.`);
  }

  if (!response.result?.success) {
    throw new Error(`Command '${command.action}' returned an error: ${response.result?.error ?? 'unknown error'}.`);
  }

  return response.result;
}

async function expectCommandFailure(command, expectedStatus, expectedErrorCode) {
  const response = await sendCommand(command);
  assert(
    !response.ok || !response.result?.success,
    `Command '${command.action}' was expected to fail but succeeded.`);
  assert(
    response.status === expectedStatus,
    `Command '${command.action}' returned HTTP ${response.status}, expected ${expectedStatus}.`);
  assert(
    response.result?.errorCode === expectedErrorCode,
    `Command '${command.action}' returned error code '${response.result?.errorCode}', expected '${expectedErrorCode}'.`);

  return response.result;
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

async function sendRuntimeMessage(extensionPage, message) {
  const response = await extensionPage.evaluate(async payload => {
    return chrome.runtime.sendMessage(payload);
  }, message);

  assert(response?.ok, `Runtime message '${message.type}' failed: ${response?.error ?? 'unknown error'}.`);
  return response;
}

async function waitForPublishedTab(tabId, shouldExist) {
  for (let attempt = 0; attempt < 30; attempt += 1) {
    const agents = await getAgents();
    const agent = agents.find(candidate => (candidate.tabs ?? []).some(tab => tab.tabId === tabId));
    const tab = agent?.tabs?.find(candidate => candidate.tabId === tabId) ?? null;

    if (shouldExist && agent && tab) {
      return { agent, tab };
    }

    if (!shouldExist && !tab) {
      return { agent: agent ?? null, tab: null };
    }

    await new Promise(resolve => setTimeout(resolve, 1000));
  }

  throw new Error(
    shouldExist
      ? `Timed out waiting for tab ${tabId} to be published by the browser agent.`
      : `Timed out waiting for tab ${tabId} to disappear from the published browser tabs.`);
}

async function readDropCount(page, selector) {
  return page.evaluate(targetSelector => {
    return Number(document.querySelector(targetSelector)?.dataset.dropCount ?? '0');
  }, selector);
}

async function assertDragSuccess(page, agentId, tabId, button, targetSelector, moveSteps) {
  const previousCount = await readDropCount(page, targetSelector);

  await executeCommand({
    agentId,
    tabId,
    action: 'locatorDragTo',
    sourceSelector: '#drag-source',
    targetSelector,
    button,
    moveSteps
  });

  await page.waitForFunction(
    ({ selector, expectedCount, expectedButton }) => {
      const target = document.querySelector(selector);
      const status = document.querySelector('#drag-status');
      if (!(target instanceof HTMLElement) || !(status instanceof HTMLElement)) {
        return false;
      }

      return target.dataset.dropCount === String(expectedCount)
        && target.dataset.lastDrop === 'true'
        && status.dataset.lastResult === 'success'
        && status.dataset.lastButton === expectedButton
        && status.dataset.lastTarget === target.id
        && Number(status.dataset.moveCount ?? '0') > 0;
    },
    {
      selector: targetSelector,
      expectedCount: previousCount + 1,
      expectedButton: button
    },
    { timeout: 15000 }
  );
}

async function waitForViewport(agentId, tabId, expectedViewport, timeoutMs = 15000) {
  const startedAt = Date.now();
  let lastViewport = null;

  while (Date.now() - startedAt <= timeoutMs) {
    const viewport = await evaluateValue(
      agentId,
      tabId,
      '({ width: window.innerWidth, height: window.innerHeight })');
    lastViewport = viewport;

    if (viewport.width === expectedViewport.width && viewport.height === expectedViewport.height) {
      return viewport;
    }

    await new Promise(resolve => setTimeout(resolve, 250));
  }

  throw new Error(
    `Timed out waiting for viewport ${JSON.stringify(expectedViewport)}. Last viewport: ${JSON.stringify(lastViewport)}.`);
}

async function waitForViewportToDiffer(agentId, tabId, previousViewport, timeoutMs = 15000) {
  const startedAt = Date.now();
  let lastViewport = null;

  while (Date.now() - startedAt <= timeoutMs) {
    const viewport = await evaluateValue(
      agentId,
      tabId,
      '({ width: window.innerWidth, height: window.innerHeight })');
    lastViewport = viewport;

    if (viewport.width !== previousViewport.width || viewport.height !== previousViewport.height) {
      return viewport;
    }

    await new Promise(resolve => setTimeout(resolve, 250));
  }

  throw new Error(
    `Timed out waiting for viewport to differ from ${JSON.stringify(previousViewport)}. Last viewport: ${JSON.stringify(lastViewport)}.`);
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
    await extensionPage.goto(`chrome-extension://${extensionId}/index.html`);

    const authorization = await extensionPage.evaluate(async targetSmokeUrl => {
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
    }, smokeUrl);

    assert(authorization?.status?.ok, `Failed to authorize smoke tab: ${authorization?.status?.error ?? 'unknown error'}.`);

    const published = await waitForPublishedTab(authorization.tabId, true);
    const agent = published.agent;
    const tab = published.tab;

    assert(Boolean(agent?.agentId), 'The registered agent does not expose agentId.');
    assert(Boolean(tab?.tabId), 'The smoke page tab was not published by the extension.');

    await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorClick',
      selector: '#click-target'
    });

    await page.waitForFunction(
      () => document.querySelector('#click-status')?.dataset.clickCount === '1',
      null,
      { timeout: 15000 });

    await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorPress',
      selector: '#press-target',
      key: 'Enter'
    });

    await page.waitForFunction(
      () => document.querySelector('#press-status')?.dataset.lastKey === 'Enter',
      null,
      { timeout: 15000 });

    const setTextResult = await executeCommand({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'setText',
      selector: '#target-textarea',
      text: 'Playwright smoke text'
    });

    assert(setTextResult.success, 'setText command did not return success.');

    await page.waitForFunction(
      expected => document.querySelector('#target-textarea')?.value === expected,
      'Playwright smoke text',
      { timeout: 15000 });

    await assertDragSuccess(page, agent.agentId, tab.tabId, 'left', '#drop-left', 1);
    await assertDragSuccess(page, agent.agentId, tab.tabId, 'middle', '#drop-middle', 12);
    await assertDragSuccess(page, agent.agentId, tab.tabId, 'right', '#drop-right', 6);

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#missing-source',
      targetSelector: '#drop-left',
      button: 'left',
      moveSteps: 4,
      timeoutMs: 1500
    }, 502, 'element_not_found');

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#drag-source',
      targetSelector: '#missing-target',
      button: 'left',
      moveSteps: 4,
      timeoutMs: 1500
    }, 502, 'element_not_found');

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#hidden-source',
      targetSelector: '#drop-left',
      button: 'left',
      moveSteps: 4,
      timeoutMs: 1500
    }, 502, 'element_not_visible');

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#drag-source',
      targetSelector: '#hidden-target',
      button: 'left',
      moveSteps: 4,
      timeoutMs: 1500
    }, 502, 'element_not_visible');

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#drag-source',
      targetSelector: '#drop-left',
      button: 'primary',
      moveSteps: 4
    }, 400, 'validation_failed');

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#drag-source',
      targetSelector: '#drop-left',
      button: 'left',
      moveSteps: 0
    }, 400, 'validation_failed');

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#drag-source',
      targetSelector: '#drop-left',
      button: 'left',
      moveSteps: 100,
      timeoutMs: 10
    }, 504, 'timeout');

    await sendRuntimeMessage(extensionPage, {
      type: 'revokeTab',
      tabId: tab.tabId
    });

    await waitForPublishedTab(tab.tabId, false);

    await expectCommandFailure({
      agentId: agent.agentId,
      tabId: tab.tabId,
      action: 'locatorDragTo',
      sourceSelector: '#drag-source',
      targetSelector: '#drop-left',
      button: 'left',
      moveSteps: 4
    }, 403, 'tab_not_authorized');

    await sendRuntimeMessage(extensionPage, {
      type: 'authorizeTab',
      tabId: tab.tabId
    });

    await waitForPublishedTab(tab.tabId, true);

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

    const portraitViewport = await waitForViewport(
      agent.agentId,
      tab.tabId,
      iPhone12ProPortrait);

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

    await waitForViewport(
      agent.agentId,
      tab.tabId,
      landscapeViewport);

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

    const clearedViewport = await waitForViewportToDiffer(
      agent.agentId,
      tab.tabId,
      landscapeViewport);

    assert(
      Math.abs(clearedViewport.width - initialViewport.width) <= 100
      && Math.abs(clearedViewport.height - initialViewport.height) <= 100
      && (clearedViewport.width !== landscapeViewport.width || clearedViewport.height !== landscapeViewport.height),
      `Viewport override did not clear close to the original desktop size. Actual: ${JSON.stringify(clearedViewport)}. Expected near: ${JSON.stringify(initialViewport)}.`);

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
