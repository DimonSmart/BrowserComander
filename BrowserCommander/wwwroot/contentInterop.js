function createBackgroundStatusFallback(error) {
    return {
        ok: false,
        agentId: "",
        connected: false,
        allowedTabs: [],
        error: typeof error === "string"
            ? error
            : String(error ?? "Background worker did not respond.")
    };
}

function createExtensionSettingsFallback(error) {
    return {
        serverAddress: "",
        defaultServerAddress: "",
        commandTimeoutMs: 30000,
        defaultCommandTimeoutMs: 30000,
        error: typeof error === "string"
            ? error
            : String(error ?? "Background worker did not respond.")
    };
}

async function sendBackgroundMessage(message, fallbackFactory) {
    try {
        const result = await chrome.runtime.sendMessage(message);
        return result ?? fallbackFactory("Background worker did not respond.");
    } catch (error) {
        return fallbackFactory(error);
    }
}

async function getBackgroundAgentStatus() {
    return sendBackgroundMessage(
        { type: "status" },
        createBackgroundStatusFallback);
}

async function getExtensionSettings() {
    return sendBackgroundMessage(
        { type: "getExtensionSettings" },
        createExtensionSettingsFallback);
}

async function saveExtensionSettings(serverAddress, commandTimeoutMs) {
    return sendBackgroundMessage({
        type: "saveExtensionSettings",
        serverAddress: serverAddress,
        commandTimeoutMs: commandTimeoutMs
    }, createBackgroundStatusFallback);
}

async function authorizeTab(tabId) {
    return sendBackgroundMessage({
        type: "authorizeTab",
        tabId: tabId
    }, createBackgroundStatusFallback);
}

async function revokeTab(tabId) {
    return sendBackgroundMessage({
        type: "revokeTab",
        tabId: tabId
    }, createBackgroundStatusFallback);
}

async function clearAuthorizedTabs() {
    return sendBackgroundMessage({
        type: "clearAuthorizedTabs"
    }, createBackgroundStatusFallback);
}

function createGlobalPagesResultFallback(error) {
    return {
        ok: false,
        error: typeof error === "string"
            ? error
            : String(error ?? "Background worker did not respond."),
        pages: []
    };
}

async function getGlobalPages() {
    return sendBackgroundMessage(
        { type: "getGlobalPages" },
        createGlobalPagesResultFallback);
}
