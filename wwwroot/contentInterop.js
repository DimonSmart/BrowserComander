function executeContentScript() {
    console.log('Attempting to inject content script');
    chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) {
        chrome.scripting.executeScript({
            target: { tabId: tabs[0].id },
            files: ['injectedScript.js']
        }, () => {
            console.log('Content script injected');
        });
    });
}
