function executeContentScript(selector, textToType) {
    console.log('Attempting to send message to content script');
    chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) {
        chrome.scripting.executeScript({
            target: { tabId: tabs[0].id },
            files: ['injectedScript.js']
        }, () => {
            chrome.tabs.sendMessage(tabs[0].id, { action: 'updateText', selector: selector, text: textToType });
            console.log('Message sent to content script');
        });
    });
}
