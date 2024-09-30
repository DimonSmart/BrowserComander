chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message.action === 'updateText') {
        const editableDiv = document.querySelector(message.selector);
        if (editableDiv) {
            editableDiv.innerHTML = message.text;
            const event = new Event('input', { bubbles: true });
            editableDiv.dispatchEvent(event);
        } else {
            console.warn(`Element with selector '${message.selector}' not found.`);
        }
    } else if (message.action === 'getText') {
        const editableDiv = document.querySelector(message.selector);
        if (editableDiv) {
            // Retrun empty string if element found and text is empty
            const text = editableDiv.innerText || '';
            sendResponse({ text: text });
        } else {
            // Return null if element not found
            sendResponse({ text: null });
        }
    }
    return true;
});
