(function () {
    // Locate the contenteditable div by its id or other attributes
    const editableDiv = document.querySelector('div#prompt-textarea[contenteditable="true"]');

    if (editableDiv) {
        // Set the inner HTML of the contenteditable div
        editableDiv.innerHTML = "Hello world";

        // Optionally, trigger the input event to simulate user typing if needed
        const event = new Event('input', { bubbles: true });
        editableDiv.dispatchEvent(event);
    }
})();
