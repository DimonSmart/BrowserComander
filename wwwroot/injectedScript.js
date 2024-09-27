if (message.action === 'updateText') {
    const editableDiv = document.querySelector(message.selector);
    if (editableDiv) {
        editableDiv.innerHTML = message.text;
        const event = new Event('input', { bubbles: true });
        editableDiv.dispatchEvent(event);
        sendResponse({ success: true });
    } else {
        console.warn(`Element with selector '${message.selector}' not found.`);
        sendResponse({ success: false });
    }
} else if (message.action === 'getText') {
    const editableDiv = document.querySelector(message.selector);
    if (editableDiv) {
        const text = editableDiv.innerText || '';
        sendResponse({ text: text });
    } else {
        sendResponse({ text: null });
    }
}
