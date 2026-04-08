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

async function getServerAddressSettings() {
    return chrome.runtime.sendMessage({
        type: "getServerAddressSettings"
    });
}

async function setServerAddress(serverAddress) {
    return chrome.runtime.sendMessage({
        type: "setServerAddress",
        serverAddress: serverAddress
    });
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
