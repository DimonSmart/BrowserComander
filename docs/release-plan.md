# BrowserCommander Release Plan

This document fixes the current publication scheme in one place so the release flow is repeatable and predictable.

## Publication topology

BrowserCommander is published through two GitHub surfaces:

1. GitHub Pages
   Purpose: public installation and troubleshooting site.
   Source: `docs/`
   Result: static site with install, MCP config, privacy, support, and troubleshooting pages.

2. GitHub Releases
   Purpose: versioned downloadable artifacts.
   Source: built by GitHub Actions from the solution and `distribution/bundle/`.
   Result: release assets plus generated release notes.

Pages and Releases are intentionally separate:

- Pages always describes the latest public installation flow.
- The site links to `releases/latest`, so the documentation does not need hardcoded version numbers.
- The versioned binaries themselves live only in GitHub Releases.

## What is published

### GitHub Pages

Publish the entire `docs/` folder:

- `docs/index.html`
- `docs/install-extension.html`
- `docs/install-server.html`
- `docs/mcp-config.html`
- `docs/troubleshooting.html`
- `docs/privacy.html`
- `docs/support.html`
- `docs/assets/site.css`

Nothing from the server, bridge, or extension binaries goes to Pages.

### GitHub Release assets

Each release tag `vX.Y.Z` publishes exactly these files:

- `browsercommander-extension-unpacked-vX.Y.Z.zip`
  Contains the unpacked browser extension build from `BrowserCommander/bin/Release/net8.0/browserextension`.
- `browsercommander-windows-x64-portable-vX.Y.Z.zip`
  Contains:
  - `BrowserCommanderServer.exe`
  - `BrowserCommander.McpStdioBridge.exe`
  - files copied from `distribution/bundle/`
- `SHA256SUMS.txt`
  Contains SHA-256 checksums for both zip files.

## Source structure and responsibilities

### Runtime projects

- `BrowserCommander/`
  Browser extension project. Release version is taken from `BrowserCommander/wwwroot/manifest.json`.
- `BrowserCommanderServer/`
  Local HTTP MCP server.
- `BrowserCommander.McpStdioBridge/`
  Stdio proxy for MCP clients that expect stdio instead of HTTP.
- `distribution/bundle/`
  Files copied into the portable bundle, including `README.txt` and MCP config examples for local clients and temporary ChatGPT remote testing.

### Release scripts

- `scripts/build-release-assets.ps1`
  Builds the extension, publishes the server and stdio bridge, assembles the portable bundle, creates zip files, and writes `SHA256SUMS.txt`.
- `scripts/Test-ReleaseBundle.ps1`
  Verifies that the expected release files exist, checks the checksums, and inspects the zip contents.

## GitHub Actions responsibilities

### `.github/workflows/pages.yml`

Purpose: publish the installation site to GitHub Pages.

Behavior:

- triggers on changes under `docs/**`
- can also be started manually with `workflow_dispatch`
- deploys only when the run comes from the repository default branch, or when started manually
- uploads `docs/` as the Pages artifact
- deploys that artifact to the `github-pages` environment

This workflow is branch-agnostic on purpose. It follows the repository default branch instead of hardcoding `main`, `master`, or `Temp`.

### `.github/workflows/release.yml`

Purpose: publish a versioned prerelease when a SemVer tag is pushed.

Behavior:

- triggers on `push` for tags matching `v*.*.*`
- supports `workflow_dispatch` as a recovery path for an already existing tag version
- resolves version `X.Y.Z` from the pushed tag `vX.Y.Z`
- runs `scripts/build-release-assets.ps1`
- runs `scripts/Test-ReleaseBundle.ps1`
- uploads the built files as a workflow artifact
- creates or updates the GitHub Release for the same tag
- marks the release as `prerelease` because the current public distribution is still alpha

## One-time GitHub repository setup

Before the first public release:

1. In repository settings, configure GitHub Pages to use `GitHub Actions` as the source.
2. Make sure the default branch is the branch that should publish `docs/`.
3. Make sure pushes of annotated tags to `origin` are allowed from the maintainer machine.

## Release sequence

### 1. Prepare the release commit

1. Update the extension version in `BrowserCommander/wwwroot/manifest.json`.
2. Update any user-facing docs in `docs/` if install or packaging changed.
3. Commit and push the release-ready state to the default branch.

### 2. Run local release checks

Build the exact release assets locally:

```powershell
.\scripts\build-release-assets.ps1 -Version 0.1.2
```

Validate the output:

```powershell
.\scripts\Test-ReleaseBundle.ps1 -Version 0.1.2
```

If build outputs are locked, stop the running BrowserCommander processes and retry before tagging.

### 3. Create and push the release tag

Create an annotated tag that matches the manifest version:

```powershell
git tag -a v0.1.2 -m "BrowserCommander v0.1.2"
git push origin v0.1.2
```

This is the actual publication trigger.

### 4. What happens after the tag push

1. GitHub Actions starts `.github/workflows/release.yml`.
2. The workflow rebuilds the release assets from the tagged commit.
3. The workflow validates the zips and checksums.
4. The workflow publishes or refreshes the GitHub Release for `v0.1.2`.
5. The Pages site remains on the current `docs/` content from the default branch and continues linking to `releases/latest`.

### 5. Post-release verification

After the workflow succeeds, verify:

1. `https://github.com/DimonSmart/BrowserComander/releases/latest` opens the new release.
2. The release contains both zip files and `SHA256SUMS.txt`.
3. The extension zip contains `manifest.json`.
4. The portable zip contains:
   - `BrowserCommanderServer.exe`
   - `BrowserCommander.McpStdioBridge.exe`
   - `README.txt`
   - `config-examples/`
5. The GitHub Pages site opens and the install buttons still lead to the latest release page.

## Installation smoke after release

Recommended manual smoke:

1. Download the two release zips from the published release.
2. Extract the portable bundle to a clean folder.
3. Start `BrowserCommanderServer.exe`, or launch `BrowserCommander.McpStdioBridge.exe` through an MCP client.
4. Extract the extension zip and load it through browser developer mode.
5. Open the extension options page and confirm the server address.
6. Authorize a tab and run the browser end-to-end smoke check.

## Operational rules

- The release tag version must exactly match `BrowserCommander/wwwroot/manifest.json`.
- Public binaries are produced only by the release workflow from a tagged commit.
- Pages is documentation-only. Do not upload binaries to the Pages site.
- The release workflow is rerunnable for an existing tag because it updates assets with `--clobber`.
