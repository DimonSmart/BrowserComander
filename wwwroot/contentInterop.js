function setTextFunctionScript(selector, textToType) {
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

function getTextFunctionScript(selector) {
    return new Promise((resolve, reject) => {
        console.log('Attempting to send message to content script to get text');
        chrome.tabs.query({ active: true, currentWindow: true }, function (tabs) {
            chrome.scripting.executeScript({
                target: { tabId: tabs[0].id },
                files: ['injectedScript.js']
            }, () => {
                chrome.tabs.sendMessage(tabs[0].id, { action: 'getText', selector: selector }, function (response) {
                    if (response && response.text !== null) {
                        console.log('Text received from content script:', response.text);
                        resolve(response.text); // Return the text to Blazor
                    } else {
                        console.warn('No text found or element not found');
                        resolve(null);
                    }
                });
            });
        });
    });
}



