chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (message.action === 'updateText') {
        const editableDiv = document.querySelector('div#prompt-textarea[contenteditable="true"]');
        if (editableDiv) {
            editableDiv.innerHTML = message.text;
            const event = new Event('input', { bubbles: true });
            editableDiv.dispatchEvent(event);
        }
    }
});
