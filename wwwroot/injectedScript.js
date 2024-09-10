(function () {
    // Locate the textarea element by its id or other attributes
    const textarea = document.querySelector('textarea#prompt-textarea');

    if (textarea) {
        console.log("Found textarea, entering text...");

        // Set the value of the textarea
        textarea.value = "Hello world";

        // Optionally, trigger the input event to simulate user typing
        const event = new Event('input', { bubbles: true });
        textarea.dispatchEvent(event);

        console.log("Text entered into the textarea: Hello world");
    } else {
        console.log("Textarea not found.");
    }
})();
