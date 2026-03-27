async function ensureInjectedScript(tabId) {
    await chrome.scripting.executeScript({
        target: { tabId: tabId },
        files: ["injectedScript.js"]
    });
}

async function setTextFunctionScript(selector, textToType) {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab || !tab.id) {
        return null;
    }

    await ensureInjectedScript(tab.id);
    return chrome.tabs.sendMessage(tab.id, {
        action: "setText",
        selector: selector,
        text: textToType
    });
}

async function getTextFunctionScript(selector) {
    const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
    if (!tab || !tab.id) {
        return null;
    }

    await ensureInjectedScript(tab.id);

    const response = await chrome.tabs.sendMessage(tab.id, {
        action: "getText",
        selector: selector
    });

    return response ? response.text ?? null : null;
}

async function getBackgroundAgentStatus() {
    try {
        return await chrome.runtime.sendMessage({ type: "status" });
    } catch (error) {
        return {
            ok: false,
            connected: false,
            error: String(error)
        };
    }
}

async function authorizeTab(tabId) {
    return chrome.runtime.sendMessage({
        type: "authorizeTab",
        tabId: tabId
    });
}

async function revokeTab(tabId) {
    return chrome.runtime.sendMessage({
        type: "revokeTab",
        tabId: tabId
    });
}

async function clearAuthorizedTabs() {
    return chrome.runtime.sendMessage({
        type: "clearAuthorizedTabs"
    });
}
