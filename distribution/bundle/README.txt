BrowserCommander Windows x64 portable bundle

Contents
- BrowserCommanderServer.exe
- BrowserCommander.McpStdioBridge.exe
- config-examples\

Quick start
1. Extract this bundle to a writable folder, for example:
   C:\Tools\BrowserCommander
2. Start BrowserCommanderServer.exe manually, or let BrowserCommander.McpStdioBridge.exe
   auto-start the sibling server when your MCP client launches it.
3. Open the BrowserCommander extension options page and confirm the local server
   address is http://localhost:5082.
4. Load the unpacked extension package from the separate release asset.
5. For temporary remote ChatGPT testing, see:
   config-examples\chatgpt-devtunnel.example.txt

Notes
- This alpha build is local-only. Use loopback addresses such as localhost or 127.0.0.1.
- The MCP HTTP endpoint is http://localhost:5082/mcp
- Replace placeholder paths in config-examples\ with the actual folder where you extracted this bundle.
