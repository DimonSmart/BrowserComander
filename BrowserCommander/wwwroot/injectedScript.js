"use strict";

(function () {
    console.log(`Message from Injected script`);

    // Кэшируем оригинальные методы из window.document
    const originalGetElementById = window.document.getElementById;
    const originalCreateElement = window.document.createElement;
    const originalQuerySelector = window.document.querySelector;
    const originalAppendChild = window.Node.prototype.appendChild;

    // Проверяем наличие методов
    console.log('originalGetElementById:', typeof originalGetElementById);
    console.log('originalCreateElement:', typeof originalCreateElement);
    console.log('originalQuerySelector:', typeof originalQuerySelector);
    console.log('originalAppendChild:', typeof originalAppendChild);

    function addBanner() {
        // Проверяем, что необходимые методы существуют и являются функциями
        if (typeof originalGetElementById !== 'function' || typeof originalCreateElement !== 'function' || typeof originalAppendChild !== 'function') {
            console.error('One of the original methods is undefined or not a function');
            return;
        }

        // Проверяем, существует ли баннер
        if (!originalGetElementById.call(document, 'content-script-banner')) {
            // Создаем баннер
            const infoBanner = originalCreateElement.call(document, 'div');
            infoBanner.id = 'content-script-banner';
            infoBanner.textContent = 'Content Script Loaded';
            infoBanner.style.position = 'fixed';
            infoBanner.style.top = '0';
            infoBanner.style.left = '0';
            infoBanner.style.width = '100%';
            infoBanner.style.backgroundColor = 'yellow';
            infoBanner.style.color = 'black';
            infoBanner.style.zIndex = '2147483647'; // Максимальный z-index
            infoBanner.style.textAlign = 'center';
            infoBanner.style.padding = '5px 0';
            infoBanner.style.fontSize = '16px';
            infoBanner.style.fontWeight = 'bold';
            infoBanner.style.fontFamily = 'Arial, sans-serif';

            // Добавляем баннер в документ
            originalAppendChild.call(document.body, infoBanner);
        }
    }

    // Добавляем баннер при загрузке скрипта
    addBanner();

    // Периодически проверяем наличие баннера и добавляем его при необходимости
    const bannerInterval = setInterval(() => {
        addBanner();
    }, 1000); // Проверяем каждую секунду

    // Останавливаем проверку после определенного времени (например, через 1 минуту)
    setTimeout(() => {
        clearInterval(bannerInterval);
    }, 60000); // Останавливаем через 60 секунд

    // Обработчик сообщений от расширения
    chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
        if (message.action === 'updateText') {
            // Проверяем, что originalQuerySelector существует
            if (typeof originalQuerySelector !== 'function') {
                console.error('originalQuerySelector is undefined or not a function');
                return;
            }

            const editableDiv = originalQuerySelector.call(document, message.selector);
            if (editableDiv) {
                editableDiv.innerHTML = message.text;
                const event = new Event('input', { bubbles: true });
                editableDiv.dispatchEvent(event);
            } else {
                console.warn(`Element with selector '${message.selector}' not found.`);
            }
        } else if (message.action === 'getText') {
            // Проверяем, что originalQuerySelector существует
            if (typeof originalQuerySelector !== 'function') {
                console.error('originalQuerySelector is undefined or not a function');
                sendResponse({ text: null });
                return;
            }

            const editableDiv = originalQuerySelector.call(document, message.selector);
            if (editableDiv) {
                const text = editableDiv.innerText || '';
                sendResponse({ text: text });
            } else {
                sendResponse({ text: null });
            }
        }
        return true;
    });
})();
