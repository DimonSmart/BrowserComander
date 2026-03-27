"use strict";

(() => {
    if (globalThis.__browserCommanderInjected) {
        return;
    }

    globalThis.__browserCommanderInjected = true;

    const querySelector = Document.prototype.querySelector;
    const querySelectorAll = Document.prototype.querySelectorAll;

    chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
        void handleMessageAsync(message)
            .then(sendResponse)
            .catch(error => {
                sendResponse(failure(
                    error?.browserCommanderCode ?? "execution_failed",
                    error?.message ?? String(error)));
            });

        return true;
    });

    async function handleMessageAsync(message) {
        const action = normalizeAction(message?.action);
        const selector = message?.selector;
        const timeoutMs = normalizeTimeout(message?.timeoutMs);
        const waitState = message?.waitState;

        switch (action) {
            case "pageFindLocators":
                return handleFindLocators(
                    message?.query,
                    message?.onlyVisible !== false,
                    message?.interactiveOnly !== false,
                    normalizeLimit(message?.limit));
            case "exists":
            case "locatorExists":
                return handleExists(selector);
            case "locatorCount":
                return handleCount(selector);
            case "locatorIsVisible":
                return handleIsVisible(selector);
            case "locatorWaitFor":
                return handleWaitFor(selector, waitState, timeoutMs);
            case "locatorFocus":
                return handleFocus(selector, timeoutMs);
            case "setText":
            case "locatorFill":
                return handleFill(selector, message?.text, timeoutMs);
            case "getText":
                return handleLegacyGetText(selector, timeoutMs);
            case "locatorInnerText":
                return handleInnerText(selector, timeoutMs);
            case "locatorTextContent":
                return handleTextContent(selector, timeoutMs);
            case "getHtml":
                return handleLegacyGetHtml(selector, timeoutMs);
            case "locatorInnerHtml":
                return handleInnerHtml(selector, timeoutMs);
            case "locatorInputValue":
                return handleInputValue(selector, timeoutMs);
            case "click":
            case "locatorClick":
                return handleClick(selector, timeoutMs);
            case "pageContent":
                return success({
                    html: document.documentElement?.outerHTML ?? "",
                    url: location.href
                });
            default:
                return failure("unsupported_action", `Unsupported action '${message?.action}'.`);
        }
    }

    function handleFindLocators(query, onlyVisible, interactiveOnly, limit) {
        const candidates = collectLocatorCandidates(query, onlyVisible, interactiveOnly, limit);
        return success({
            valueJson: JSON.stringify(candidates)
        });
    }

    function handleExists(selector) {
        const count = countElements(selector);
        return success({
            exists: count > 0,
            count
        });
    }

    function handleCount(selector) {
        const count = countElements(selector);
        return success({
            exists: count > 0,
            count
        });
    }

    function handleIsVisible(selector) {
        const element = findFirst(selector);
        const visible = element ? isElementVisible(element) : false;
        return success({
            exists: Boolean(element),
            visible
        });
    }

    async function handleWaitFor(selector, waitState, timeoutMs) {
        const state = normalizeWaitState(waitState);
        const result = await waitForSelectorState(selector, state, timeoutMs);
        return success({
            exists: result.exists,
            visible: result.visible
        });
    }

    async function handleFill(selector, value, timeoutMs) {
        const element = await waitForElement(selector, "visible", timeoutMs);
        fillElement(element, value ?? "");

        return success({
            exists: true,
            visible: isElementVisible(element),
            text: readLegacyText(element)
        });
    }

    async function handleFocus(selector, timeoutMs) {
        const element = await waitForElement(selector, "visible", timeoutMs);
        focusElement(element);

        return success({
            exists: true,
            visible: isElementVisible(element)
        });
    }

    async function handleLegacyGetText(selector, timeoutMs) {
        const element = await waitForElement(selector, "attached", timeoutMs);
        return success({
            exists: true,
            visible: isElementVisible(element),
            text: readLegacyText(element)
        });
    }

    async function handleInnerText(selector, timeoutMs) {
        const element = await waitForElement(selector, "attached", timeoutMs);
        return success({
            exists: true,
            visible: isElementVisible(element),
            text: element.innerText ?? ""
        });
    }

    async function handleTextContent(selector, timeoutMs) {
        const element = await waitForElement(selector, "attached", timeoutMs);
        return success({
            exists: true,
            visible: isElementVisible(element),
            text: element.textContent ?? ""
        });
    }

    async function handleLegacyGetHtml(selector, timeoutMs) {
        const element = await waitForElement(selector, "attached", timeoutMs);
        return success({
            exists: true,
            visible: isElementVisible(element),
            html: element.outerHTML ?? ""
        });
    }

    async function handleInnerHtml(selector, timeoutMs) {
        const element = await waitForElement(selector, "attached", timeoutMs);
        return success({
            exists: true,
            visible: isElementVisible(element),
            html: element.innerHTML ?? ""
        });
    }

    async function handleInputValue(selector, timeoutMs) {
        const element = await waitForElement(selector, "attached", timeoutMs);
        if (!supportsValueProperty(element)) {
            return failure(
                "element_not_editable",
                `Element '${selector}' does not expose a value property.`);
        }

        return success({
            exists: true,
            visible: isElementVisible(element),
            text: element.value ?? ""
        });
    }

    async function handleClick(selector, timeoutMs) {
        const element = await waitForElement(selector, "visible", timeoutMs);
        if (isElementDisabled(element)) {
            return failure(
                "execution_failed",
                `Element '${selector}' is disabled and cannot be clicked.`);
        }

        focusElement(element);

        element.click();

        return success({
            exists: true,
            visible: true
        });
    }

    async function waitForElement(selector, waitState, timeoutMs) {
        const result = await waitForSelectorState(selector, waitState, timeoutMs);
        if (!result.element) {
            throw createCommandError(
                "element_not_found",
                `Element '${selector}' was not found.`);
        }

        if (waitState === "visible" && !result.visible) {
            throw createCommandError(
                "element_not_visible",
                `Element '${selector}' is not visible.`);
        }

        return result.element;
    }

    async function waitForSelectorState(selector, waitState, timeoutMs) {
        const deadline = Date.now() + timeoutMs;

        while (Date.now() <= deadline) {
            const elements = findAll(selector);
            const element = elements[0] ?? null;
            const exists = elements.length > 0;
            const visible = Boolean(element) && isElementVisible(element);

            if (matchesWaitState(waitState, exists, visible)) {
                return {
                    element,
                    exists,
                    visible
                };
            }

            await delay(100);
        }

        throw createCommandError(
            "timeout",
            `Timed out after ${timeoutMs} ms waiting for selector '${selector}' to become '${waitState}'.`);
    }

    function matchesWaitState(waitState, exists, visible) {
        switch (waitState) {
            case "attached":
                return exists;
            case "detached":
                return !exists;
            case "visible":
                return exists && visible;
            case "hidden":
                return !exists || !visible;
            default:
                return false;
        }
    }

    function fillElement(element, text) {
        focusElement(element);

        if (element instanceof HTMLInputElement) {
            if (!supportsFillInputType(element.type)) {
                throw createCommandError(
                    "element_not_editable",
                    `Input type '${element.type}' does not support fill.`);
            }

            element.value = text;
        } else if (element instanceof HTMLTextAreaElement) {
            element.value = text;
        } else if (element instanceof HTMLSelectElement) {
            element.value = text;
        } else if (element.isContentEditable) {
            element.textContent = text;
        } else {
            throw createCommandError(
                "element_not_editable",
                `Element '${element.tagName.toLowerCase()}' does not support fill.`);
        }

        dispatchInputEvents(element);
    }

    function supportsFillInputType(type) {
        return new Set([
            "",
            "email",
            "number",
            "password",
            "search",
            "tel",
            "text",
            "url"
        ]).has(String(type ?? "").toLowerCase());
    }

    function dispatchInputEvents(element) {
        element.dispatchEvent(new Event("input", { bubbles: true }));
        element.dispatchEvent(new Event("change", { bubbles: true }));
    }

    function supportsValueProperty(element) {
        return typeof element?.value === "string";
    }

    function readLegacyText(element) {
        if (supportsValueProperty(element)) {
            return element.value ?? "";
        }

        return element.innerText ?? element.textContent ?? "";
    }

    function isElementVisible(element) {
        if (!(element instanceof Element)) {
            return false;
        }

        const style = getComputedStyle(element);
        if (style.display === "none"
            || style.visibility === "hidden"
            || style.visibility === "collapse"
            || style.opacity === "0") {
            return false;
        }

        const rect = element.getBoundingClientRect();
        if (rect.width <= 0 || rect.height <= 0) {
            return false;
        }

        return element.getClientRects().length > 0;
    }

    function isElementDisabled(element) {
        return Boolean(element?.disabled)
            || element?.getAttribute?.("aria-disabled") === "true";
    }

    function findFirst(selector) {
        if (!isNonEmptyString(selector)) {
            return null;
        }

        return querySelector.call(document, selector);
    }

    function findAll(selector) {
        if (!isNonEmptyString(selector)) {
            return [];
        }

        return Array.from(querySelectorAll.call(document, selector));
    }

    function countElements(selector) {
        return findAll(selector).length;
    }

    function collectLocatorCandidates(query, onlyVisible, interactiveOnly, limit) {
        const normalizedQuery = normalizeSearchQuery(query);
        const elements = interactiveOnly
            ? findInteractiveElements()
            : Array.from(document.querySelectorAll("body *"));
        const candidates = [];

        for (const element of elements) {
            const candidate = buildLocatorCandidate(element, normalizedQuery);
            if (!candidate) {
                continue;
            }

            if (onlyVisible && !candidate.visible) {
                continue;
            }

            if (normalizedQuery.hasQuery && candidate.matchedFields.length === 0) {
                continue;
            }

            candidates.push(candidate);
        }

        candidates.sort(compareLocatorCandidates);
        return candidates.slice(0, limit);
    }

    function buildLocatorCandidate(element, normalizedQuery) {
        if (!(element instanceof Element)) {
            return null;
        }

        const visible = isElementVisible(element);
        const editable = isElementEditable(element);
        const disabled = isElementDisabled(element);
        const tag = element.tagName.toLowerCase();
        const id = element.id || null;
        const role = normalizeNullableString(element.getAttribute("role"));
        const type = normalizeNullableString(element.getAttribute("type"));
        const name = normalizeNullableString(element.getAttribute("name"));
        const placeholder = normalizeNullableString(resolveElementPlaceholder(element));
        const ariaLabel = normalizeNullableString(element.getAttribute("aria-label"));
        const title = normalizeNullableString(element.getAttribute("title"));
        const text = truncateText(readCandidateText(element), 160);
        const selector = suggestSelector(element);
        if (!selector) {
            return null;
        }

        const scoring = scoreCandidate({
            tag,
            id,
            role,
            type,
            name,
            placeholder,
            ariaLabel,
            title,
            text,
            visible,
            editable,
            disabled
        }, normalizedQuery);

        return {
            selector,
            tag,
            id,
            role,
            type,
            name,
            placeholder,
            ariaLabel,
            title,
            text,
            visible,
            editable,
            disabled,
            score: scoring.score,
            matchedFields: scoring.matchedFields
        };
    }

    function findInteractiveElements() {
        const selector = [
            "input",
            "textarea",
            "select",
            "button",
            "a[href]",
            "summary",
            "[role]",
            "[contenteditable=\"true\"]",
            "[tabindex]"
        ].join(",");

        const seen = new Set();
        const elements = [];
        for (const element of document.querySelectorAll(selector)) {
            if (!(element instanceof Element)) {
                continue;
            }

            if (seen.has(element)) {
                continue;
            }

            seen.add(element);
            elements.push(element);
        }

        return elements;
    }

    function scoreCandidate(candidate, normalizedQuery) {
        let score = 0;
        const matchedFields = new Set();

        if (!normalizedQuery.hasQuery) {
            score += candidate.visible ? 40 : 0;
            score += candidate.editable ? 35 : 0;
            score += !candidate.disabled ? 10 : 0;
            score += candidate.id ? 20 : 0;
            score += candidate.role === "textbox" ? 25 : 0;
            score += candidate.tag === "button" ? 15 : 0;
            score += candidate.placeholder ? 10 : 0;
            return {
                score,
                matchedFields: []
            };
        }

        const fields = [
            { name: "id", value: candidate.id, containsWeight: 90, exactWeight: 140 },
            { name: "role", value: candidate.role, containsWeight: 50, exactWeight: 80 },
            { name: "type", value: candidate.type, containsWeight: 30, exactWeight: 45 },
            { name: "name", value: candidate.name, containsWeight: 70, exactWeight: 110 },
            { name: "placeholder", value: candidate.placeholder, containsWeight: 95, exactWeight: 140 },
            { name: "ariaLabel", value: candidate.ariaLabel, containsWeight: 95, exactWeight: 140 },
            { name: "title", value: candidate.title, containsWeight: 70, exactWeight: 110 },
            { name: "text", value: candidate.text, containsWeight: 75, exactWeight: 120 },
            { name: "tag", value: candidate.tag, containsWeight: 15, exactWeight: 20 }
        ];

        for (const field of fields) {
            const fieldValue = normalizeSearchValue(field.value);
            if (!fieldValue) {
                continue;
            }

            if (fieldValue === normalizedQuery.value) {
                score += field.exactWeight;
                matchedFields.add(field.name);
                continue;
            }

            if (fieldValue.includes(normalizedQuery.value)) {
                score += field.containsWeight;
                matchedFields.add(field.name);
            }

            for (const token of normalizedQuery.tokens) {
                if (token.length < 2 || !fieldValue.includes(token)) {
                    continue;
                }

                score += Math.max(8, Math.floor(field.containsWeight / 4));
                matchedFields.add(field.name);
            }
        }

        score += candidate.visible ? 20 : 0;
        score += candidate.editable ? 15 : 0;
        score += !candidate.disabled ? 5 : 0;

        return {
            score,
            matchedFields: Array.from(matchedFields)
        };
    }

    function compareLocatorCandidates(left, right) {
        return right.score - left.score
            || Number(right.visible) - Number(left.visible)
            || Number(right.editable) - Number(left.editable)
            || Number(!right.disabled) - Number(!left.disabled)
            || left.selector.localeCompare(right.selector);
    }

    function suggestSelector(element) {
        const selectorCandidates = [];
        const id = element.id;
        if (isNonEmptyString(id)) {
            selectorCandidates.push(`#${escapeCssValue(id)}`);
        }

        const tag = element.tagName.toLowerCase();
        const role = element.getAttribute("role");
        const name = element.getAttribute("name");
        const ariaLabel = element.getAttribute("aria-label");
        const placeholder = element.getAttribute("placeholder");
        const title = element.getAttribute("title");
        const dataTestId = element.getAttribute("data-testid") ?? element.getAttribute("data-test-id");

        if (isNonEmptyString(dataTestId)) {
            selectorCandidates.push(`[data-testid="${escapeAttributeValue(dataTestId)}"]`);
        }

        if (isNonEmptyString(name)) {
            selectorCandidates.push(`${tag}[name="${escapeAttributeValue(name)}"]`);
        }

        if (isNonEmptyString(ariaLabel)) {
            selectorCandidates.push(`${tag}[aria-label="${escapeAttributeValue(ariaLabel)}"]`);
        }

        if (isNonEmptyString(placeholder)) {
            selectorCandidates.push(`${tag}[placeholder="${escapeAttributeValue(placeholder)}"]`);
        }

        if (isNonEmptyString(title)) {
            selectorCandidates.push(`${tag}[title="${escapeAttributeValue(title)}"]`);
        }

        if (isNonEmptyString(role)) {
            selectorCandidates.push(`${tag}[role="${escapeAttributeValue(role)}"]`);
            selectorCandidates.push(`[role="${escapeAttributeValue(role)}"]`);
        }

        selectorCandidates.push(buildDomPathSelector(element));

        for (const selectorCandidate of selectorCandidates) {
            if (!isNonEmptyString(selectorCandidate)) {
                continue;
            }

            if (isUniqueSelector(selectorCandidate, element)) {
                return selectorCandidate;
            }
        }

        return null;
    }

    function buildDomPathSelector(element) {
        const segments = [];
        let current = element;

        while (current instanceof Element && segments.length < 4) {
            const tag = current.tagName.toLowerCase();
            if (isNonEmptyString(current.id)) {
                segments.unshift(`#${escapeCssValue(current.id)}`);
                break;
            }

            const parent = current.parentElement;
            if (!parent) {
                segments.unshift(tag);
                break;
            }

            const siblings = Array.from(parent.children)
                .filter(candidate => candidate.tagName === current.tagName);
            const index = siblings.indexOf(current) + 1;
            segments.unshift(`${tag}:nth-of-type(${index})`);
            current = parent;
        }

        return segments.join(" > ");
    }

    function isUniqueSelector(selector, expectedElement) {
        try {
            const matches = Array.from(document.querySelectorAll(selector));
            return matches.length === 1 && matches[0] === expectedElement;
        } catch {
            return false;
        }
    }

    function escapeCssValue(value) {
        if (typeof CSS !== "undefined" && typeof CSS.escape === "function") {
            return CSS.escape(value);
        }

        return String(value).replace(/[^a-zA-Z0-9_-]/g, match => `\\${match}`);
    }

    function escapeAttributeValue(value) {
        return String(value).replace(/\\/g, "\\\\").replace(/"/g, "\\\"");
    }

    function readCandidateText(element) {
        if (supportsValueProperty(element)) {
            return element.value ?? "";
        }

        if (element instanceof HTMLInputElement) {
            return element.value ?? "";
        }

        return collapseWhitespace(element.innerText ?? element.textContent ?? "");
    }

    function resolveElementPlaceholder(element) {
        if (!(element instanceof Element)) {
            return null;
        }

        const directPlaceholder = element.getAttribute("placeholder")
            ?? element.getAttribute("aria-placeholder")
            ?? element.getAttribute("data-placeholder");
        if (isNonEmptyString(directPlaceholder)) {
            return directPlaceholder;
        }

        const placeholderDescendant = element.querySelector("[placeholder], [aria-placeholder], [data-placeholder]");
        if (placeholderDescendant instanceof Element) {
            const descendantPlaceholder = placeholderDescendant.getAttribute("placeholder")
                ?? placeholderDescendant.getAttribute("aria-placeholder")
                ?? placeholderDescendant.getAttribute("data-placeholder");
            if (isNonEmptyString(descendantPlaceholder)) {
                return descendantPlaceholder;
            }
        }

        return null;
    }

    function focusElement(element) {
        element.scrollIntoView({
            block: "center",
            inline: "center",
            behavior: "instant"
        });

        if (typeof element.focus === "function") {
            element.focus();
        }
    }

    function collapseWhitespace(value) {
        return String(value ?? "").replace(/\s+/g, " ").trim();
    }

    function truncateTextLegacy(value, maxLength) {
        if (!isNonEmptyString(value)) {
            return null;
        }

        const trimmed = collapseWhitespace(value);
        return trimmed.length <= maxLength
            ? trimmed
            : `${trimmed.slice(0, maxLength - 1)}…`;
    }

    function truncateText(value, maxLength) {
        if (!isNonEmptyString(value)) {
            return null;
        }

        const trimmed = collapseWhitespace(value);
        return trimmed.length <= maxLength
            ? trimmed
            : `${trimmed.slice(0, Math.max(0, maxLength - 3))}...`;
    }

    function isElementEditable(element) {
        return element instanceof HTMLInputElement
            || element instanceof HTMLTextAreaElement
            || element instanceof HTMLSelectElement
            || element.isContentEditable;
    }

    function normalizeSearchQuery(query) {
        const value = normalizeSearchValue(query);
        return {
            value,
            hasQuery: value.length > 0,
            tokens: value.length > 0
                ? value.split(/\s+/).filter(token => token.length > 0)
                : []
        };
    }

    function normalizeSearchValue(value) {
        return collapseWhitespace(value).toLowerCase();
    }

    function normalizeNullableString(value) {
        return isNonEmptyString(value)
            ? String(value)
            : null;
    }

    function normalizeAction(action) {
        return action === "updateText"
            ? "setText"
            : action;
    }

    function normalizeTimeout(timeoutMs) {
        return Number.isFinite(timeoutMs) && timeoutMs > 0
            ? timeoutMs
            : 10000;
    }

    function normalizeLimit(limit) {
        return Number.isFinite(limit) && limit > 0
            ? Math.min(limit, 100)
            : 20;
    }

    function normalizeWaitState(waitState) {
        const normalized = String(waitState ?? "visible").trim().toLowerCase();
        if (normalized === "attached"
            || normalized === "detached"
            || normalized === "visible"
            || normalized === "hidden") {
            return normalized;
        }

        throw createCommandError(
            "unsupported_wait_state",
            `Unsupported waitState '${waitState}'. Supported values: attached, detached, visible, hidden.`);
    }

    function isNonEmptyString(value) {
        return typeof value === "string" && value.trim().length > 0;
    }

    function createCommandError(errorCode, message) {
        const error = new Error(message);
        error.browserCommanderCode = errorCode;
        return error;
    }

    function success(payload) {
        return {
            success: true,
            ...payload
        };
    }

    function failure(errorCode, error, payload) {
        return {
            success: false,
            errorCode,
            error,
            ...payload
        };
    }

    function delay(timeoutMs) {
        return new Promise(resolve => setTimeout(resolve, timeoutMs));
    }
})();
