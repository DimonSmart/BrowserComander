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

function createServerAddressSettingsFallback() {
    return {
        serverAddress: "",
        defaultServerAddress: ""
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

async function getServerAddressSettings() {
    return sendBackgroundMessage(
        { type: "getServerAddressSettings" },
        createServerAddressSettingsFallback);
}

async function setServerAddress(serverAddress) {
    return sendBackgroundMessage({
        type: "setServerAddress",
        serverAddress: serverAddress
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
