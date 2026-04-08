# BrowserCommander

`BrowserCommander` is a local-first browser automation prototype for the case where the browser is already open, the user is already logged in, and the target page is already in the exact state that matters.

Instead of launching a fresh browser like classic Playwright automation, this project uses a browser extension as an in-browser agent. The extension connects to a local ASP.NET Core server, and that server is exposed as an MCP server so an LLM client can inspect and manipulate the authorized page.

## Why this exists

The target use case is interactive debugging and testing of a page that is already open in a real user browser session:

- the user already completed login or MFA
- the page already contains the right application state
- the user explicitly chooses which tab may be automated
- an LLM can then use MCP tools to inspect or drive that tab

This is intentionally different from the usual "start a clean browser from code" model.

## Architecture

The solution currently has three runtime pieces:

- `BrowserCommander`
  Browser extension built with Blazor WebAssembly for the popup UI and JavaScript for the MV3 background agent.
- `BrowserCommanderServer`
  ASP.NET Core server that keeps track of connected browser agents, forwards automation commands over SignalR, and exposes MCP over HTTP at `/mcp`.
- `BrowserCommander.McpStdioBridge`
  A thin MCP stdio bridge that proxies `tools/list` and `tools/call` into the HTTP MCP endpoint. This makes the same server usable by stdio-oriented MCP clients.

Runtime flow:

1. The user opens the browser popup.
2. The user explicitly authorizes the current tab.
3. The background agent connects to the server and registers itself plus the authorized tab list.
4. An MCP client calls tools on the server.
5. The server compiles the requested high-level action into a server-driven execution plan.
6. The extension executes the plan step by step using a small set of content-script, debugger, and tab-control primitives.
7. The extension returns the merged result back to the server.

## Public alpha distribution

The first public distribution target is a Windows x64 local-only alpha:

- GitHub Pages hosts the install and troubleshooting documentation from `docs/`.
- GitHub Releases publish the downloadable assets.
- The extension is distributed as an unpacked zip that users load through browser developer mode.
- The desktop side is distributed as a portable bundle that contains:
  - `BrowserCommanderServer.exe`
  - `BrowserCommander.McpStdioBridge.exe`
  - MCP config examples

Expected release asset names:

- `browsercommander-extension-unpacked-vX.Y.Z.zip`
- `browsercommander-windows-x64-portable-vX.Y.Z.zip`
- `SHA256SUMS.txt`

Release process documentation for maintainers lives in `docs/release-plan.md`.

## Current MCP surface

The server currently exposes these MCP tools:

- `browser_list_pages`
  Lists explicitly authorized pages and returns `pageId` values used by the other tools.
- `browser_list_viewport_presets`
  Lists the built-in phone viewport presets that can be applied to an authorized page. These presets change viewport size only.
- `page_url`
  Returns the current page URL.
- `page_title`
  Returns the current page title.
- `page_content`
  Returns the raw full HTML content of the page. Treat this as a last-resort fallback after locator discovery and focused reads because full-page HTML is usually too noisy and too large for efficient LLM use.
- `page_find_locators`
  Searches the page for likely element candidates and returns suggested CSS selectors plus matching diagnostics.
- `page_evaluate`
  Evaluates JavaScript on the page through the browser debugger protocol.
- `page_screenshot`
  Captures a page screenshot through the browser debugger protocol.
- `page_console_messages`
  Returns buffered console and runtime messages collected from the authorized page.
- `page_network_requests`
  Returns buffered network activity collected from the authorized page.
- `page_set_viewport_preset`
  Applies a built-in phone viewport preset to the authorized page.
- `page_clear_viewport_override`
  Clears any active viewport-size override on the authorized page.
- `page_goto`
  Navigates the already-open page to a new URL.
- `page_reload`
  Reloads the page.
- `page_go_back`
  Goes back in history.
- `page_go_forward`
  Goes forward in history.
- `page_wait_for_url`
  Waits until the URL matches `exact`, `contains`, `glob`, or `regex`.
- `page_wait_for_load_state`
  Waits for `load` or `domcontentloaded`.
- `locator_click`
  Clicks the first matching element.
- `locator_fill`
  Fills a text-entry element.
- `locator_inner_text`
  Reads `innerText`.
- `locator_text_content`
  Reads `textContent`.
- `locator_inner_html`
  Reads `innerHTML`.
- `locator_input_value`
  Reads the current input value.
- `locator_exists`
  Checks whether the locator exists.
- `locator_count`
  Counts locator matches.
- `locator_is_visible`
  Checks whether the first match is visible.
- `locator_wait_for`
  Waits for `attached`, `detached`, `visible`, or `hidden`.

This is now a first page/locator layer rather than a raw `agentId + tabId + selector` RPC surface, but it is still not a full Playwright-equivalent API.

## Server-driven execution plans

The wire protocol is now moving toward a better split of responsibilities:

- MCP tools stay high-level and server-owned.
- The server compiles those tool calls into structured execution plans.
- The browser extension acts as a thin executor for a small primitive set:
  - `contentScript` steps for DOM-oriented primitives
  - `debugger` steps for CDP-backed primitives such as evaluate, screenshot, and key presses
  - `tab` steps for navigation and wait primitives

This means most future behavior changes should happen in the server-side plan builder rather than by adding a new bespoke action to the extension every time.

## Current constraints

- The main deployment model is local-first.
- There is no auth yet between extension and server.
- Only tabs explicitly authorized by the user may be automated.
- The popup is designed around authorizing the current tab, not multi-select.
- MCP tool calls use `pageId`, and the server now compiles them into structured execution plans before sending them to the browser agent.
- Locators currently use CSS selectors only.
- `page_find_locators` helps discover selectors, but the returned selectors are still CSS selectors.
- Debugger-backed tools depend on the browser extension having the `debugger` permission.
- Viewport presets currently override viewport size only. They do not emulate touch, mobile UA, DPR, or device frames.
- `page_console_messages` and `page_network_requests` are in-memory buffers owned by the extension background worker.
- `page_wait_for_load_state` currently supports `load` and `domcontentloaded`, not `networkidle`.
- There is still no dialog, download, upload, popup, worker, or response-body tool yet.

## Running locally

### 1. Build

```powershell
dotnet build BrowserComander.sln
```

### 2. Start the automation server

```powershell
dotnet run --project BrowserCommanderServer/BrowserCommanderServer.csproj --launch-profile http
```

By default the MCP HTTP endpoint is available at:

```text
http://localhost:5082/mcp
```

### 3. Load the browser extension

Load the unpacked extension from:

```text
BrowserCommander/bin/Debug/net8.0/browserextension
```

### 4. Authorize the current tab

Open the extension popup and click `Authorize Current Tab`.

Only authorized tabs are published to the server and available to MCP tools.

## MCP transports

### HTTP MCP

Point an MCP client at:

```text
http://localhost:5082/mcp
```

### Temporary remote ChatGPT testing

ChatGPT developer mode currently expects a remote MCP server URL, not `localhost`.
For BrowserCommander, a practical test setup is to place a temporary HTTPS tunnel in front of the local HTTP MCP endpoint:

```powershell
devtunnel user login
devtunnel host -p 5082 --allow-anonymous
```

Take the HTTPS forwarding URL shown by Dev Tunnels and append `/mcp`.
Example:

```text
https://xxxxx.euw.devtunnels.ms/mcp
```

For BrowserCommander, use `/mcp`, not `/mcp/sse`.
Keep the extension options page pointed at `http://localhost:5082`; the tunnel URL is only for the remote ChatGPT connection.

Security warning:

- `--allow-anonymous` makes the forwarded MCP endpoint publicly reachable.
- Use it only for short-lived testing.
- Do not use anonymous tunneling for sensitive browsing sessions.

### stdio MCP bridge

Run the stdio bridge:

```powershell
dotnet run --project BrowserCommander.McpStdioBridge/BrowserCommander.McpStdioBridge.csproj -- http://localhost:5082/mcp
```

If no argument is passed, the bridge defaults to `http://localhost:5082/mcp`.

You can also override the upstream endpoint with:

```text
BROWSER_COMMANDER_MCP_HTTP_URL
```

For MCP clients that want an explicit `command` / `args` shape, use:

```text
command: dotnet
args:
  run
  --project
  C:\Private\BrowserComander\BrowserCommander.McpStdioBridge\BrowserCommander.McpStdioBridge.csproj
  --no-build
  --
  http://localhost:5082/mcp
cwd: C:\Private\BrowserComander
```

## Smoke checks

HTTP MCP smoke:

```powershell
dotnet run --project scripts/BrowserCommander.McpSmoke/BrowserCommander.McpSmoke.csproj
```

stdio MCP smoke:

```powershell
dotnet run --project scripts/BrowserCommander.McpSmoke/BrowserCommander.McpSmoke.csproj -- stdio
```

Browser end-to-end smoke:

```powershell
node scripts/playwright-extension-smoke.js
```

This smoke script expects the `playwright` Node package to be available in the local environment.

## Planned low-volume page inspection

For LLM-driven workflows, a raw `page_content` dump is often the wrong first step. The preferred direction is to split page inspection into two phases:

1. Ask for a compact outline that describes the visible logical structure with reusable selectors.
2. Use one of those selectors either to drill deeper into the structure or to read only the needed content.

Final proposed tool shape:

- `page_outline(pageId, selector = "body", depth = 1, maxNodes = 40)`
  Returns a compact outline for the selected scope. Calling it with `body` gives the page-level skeleton. Calling it again with one returned `selector` drills into that region, so a separate `page_region_skeleton` tool is not needed.
- `page_read(pageId, selector, format = "text", maxChars = 4000)`
  Returns a bounded extract for one selected node or region. This is for reading content, not for structure discovery.

Proposed `page_outline` response shape:

```json
{
  "pageId": "page:<agentId>:<tabId>",
  "scopeSelector": "body",
  "url": "https://example.com/app",
  "title": "Orders",
  "truncated": false,
  "nodes": [
    {
      "selector": "#app main",
      "parentSelector": "body",
      "kind": "region",
      "tag": "main",
      "role": "main",
      "name": "Orders workspace",
      "textSample": "Orders Filters Table Export",
      "childCount": 3,
      "interactiveCount": 8,
      "operations": ["outline", "read"]
    },
    {
      "selector": "form[aria-label=\"Filters\"]",
      "parentSelector": "#app main",
      "kind": "form",
      "tag": "form",
      "role": "form",
      "name": "Filters",
      "textSample": "Status Date range Search Reset Apply",
      "childCount": 2,
      "interactiveCount": 5,
      "operations": ["outline", "read"]
    },
    {
      "selector": "#prompt-textarea",
      "parentSelector": "form[aria-label=\"Filters\"]",
      "kind": "control",
      "tag": "div",
      "role": "textbox",
      "name": "Search",
      "textSample": "",
      "childCount": 0,
      "interactiveCount": 1,
      "operations": ["read", "fill"]
    },
    {
      "selector": "button[data-testid=\"apply-filters\"]",
      "parentSelector": "form[aria-label=\"Filters\"]",
      "kind": "control",
      "tag": "button",
      "role": "button",
      "name": "Apply",
      "textSample": "Apply",
      "childCount": 0,
      "interactiveCount": 1,
      "operations": ["read", "click"]
    }
  ]
}
```

Proposed `page_read` response shape:

```json
{
  "pageId": "page:<agentId>:<tabId>",
  "selector": "table[aria-label=\"Orders\"]",
  "format": "text",
  "truncated": true,
  "text": "Order Customer Status Total #1042 Alice Paid ..."
}
```

Contract rules:

- `page_outline` must stay small and deterministic. It should prefer visible landmarks and meaningful controls over raw DOM completeness.
- `nodes` should be a flat list, not a deep recursive tree. The next level is retrieved by calling `page_outline` again with a chosen `selector`.
- Every node must include a reusable CSS `selector`.
- `textSample` should be short, for example 80 to 160 characters, and never a large dump.
- `operations` tells the LLM what to do next with the same selector:
  - `outline` means the node can be expanded with another `page_outline` call.
  - `read` means `page_read` is useful for this node.
  - `click` or `fill` means the selector can be passed directly to `locator_*`.
- `page_read` must be bounded by `maxChars` and return `truncated=true` when clipped.
- `page_content` remains a raw fallback only when this structured path is insufficient.

Recommended LLM flow:

1. `browser_list_pages`
2. `page_outline(pageId)`
3. choose a node by `selector`
4. if `operations` includes `outline`, call `page_outline(pageId, selector = chosenSelector)`
5. if `operations` includes `read`, call `page_read(pageId, selector = chosenSelector)`
6. if `operations` includes `click` or `fill`, use `locator_*` with the same selector
7. fall back to `page_content` only when the structured path is insufficient

## Roadmap to a more Playwright-like API

The current API now has a page/locator shape, but it is still far from the full Playwright model. The most important next steps are:

1. Add frame-aware primitives and frame discovery so the MCP model matches `page` / `frame` / `locator` more closely.
2. Add richer locator operations such as `press`, `hover`, `check`, `uncheck`, `select_option`, `nth`, and role/text locators instead of CSS-only selectors.
3. Add page metadata helpers such as `page_info`, plus richer screenshot options.
4. Extend the new debugger-backed observability tools with failed requests, response bodies, and optionally tracing.
5. Add explicit handling for dialogs, file upload, downloads, popups, and workers.
6. Consider a `chrome.debugger`-backed path for the observability-heavy features where DOM injection alone is not enough.

That path would move the project from a raw DOM command gateway toward a real "attached browser automation" MCP server.
