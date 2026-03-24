# Jelly Covers - AI Agent Instructions

## Project Overview

**Description**: Jellyfin plugin that provides fallback cover-image extraction for books, audiobooks, comics, magazines, and music libraries. Works alongside built-in providers as a safety net — when they fail to find a cover or crash on mislabeled embedded art, Jelly Covers steps in. As a final fallback, searches Open Library and Google Books for cover images automatically.

**Architecture Pattern**: Monolith - single deployable unit (Jellyfin plugin DLL)

**Visibility**: Public repository

### Repository

- **URL**: https://github.com/GeiserX/jelly-covers
- **Platform**: GitHub
- **Plugin GUID**: `82eef869-3f18-4678-968d-06efc10b60cf`
- **Previous name**: `jellyfin-plugin-book-cover` / "Book Cover" (renamed at v5.0.0.0)

## Technology Stack

### Languages

- C# (.NET 9.0)
- HTML / JavaScript (config page — vanilla JS, no framework)

### Frameworks & Libraries

- Jellyfin Plugin API (10.11+): `BasePlugin<T>`, `IDynamicImageProvider`, `IHasWebPages`, `IPluginServiceRegistrator`
- System.IO.Compression (EPUB ZIP archive handling)
- System.Net.Http (online cover fetching)
- System.Text.Json (Open Library / Google Books API parsing)
- System.Diagnostics.Process (pdftoppm / ffmpeg shell-out)

### External Binaries

| Binary | Required For | Discovery |
|--------|-------------|-----------|
| `pdftoppm` | PDF covers | `pdftoppm -v` — checked once, cached |
| `ffmpeg` | Audio covers | `/usr/lib/jellyfin-ffmpeg/ffmpeg` first, then PATH — cached |

Both optional. If absent, the respective extraction is silently skipped (warning logged once).

## Architecture

```
Plugin.cs                            Entry point, IHasWebPages (config UI, sidebar entry)
├── Configuration/
│   ├── PluginConfiguration.cs       Settings: DPI, JPEG quality, timeout, online fetch toggle
│   └── configPage.html              Admin UI — Jellyfin emby-* components, per-library toggle
├── CoverImageProvider.cs            IDynamicImageProvider: PDF, EPUB, audio, folder, sidecar
├── CoverStatusController.cs         REST API: GET /JellyCovers/Status
├── OnlineCoverFetcher.cs            Open Library + Google Books (last resort)
└── PluginServiceRegistrar.cs        Registers CoverImageProvider as singleton
```

### Extraction Pipeline

For each `Book` or `AudioBook` item without a cover, Jellyfin calls `CoverImageProvider.GetImage()`. Methods tried in order:

1. **EPUB** — Opens ZIP archive. 3-tier search: by filename (`cover`, `portada`, `front`, `frontcover`, `book_cover`), by path (any image with `cover` in archive path), by size (largest image >5 KB).
2. **PDF** — Shells out to `pdftoppm` for page 1 as JPEG. DPI and quality configurable.
3. **Audio file** — `ffmpeg -vcodec copy` raw-copies embedded art stream. Format detected via magic bytes (JPEG, PNG, GIF, WebP), stripping leading null padding.
4. **Audio sidecar** — Checks for `cover.jpg`, `folder.jpg`, `front.jpg`, `poster.jpg`, `thumb.jpg` next to the file.
5. **Folder audiobook** — Sidecar images in directory, then embedded art from first audio file.
6. **Online fallback** — Open Library (title + author) → Google Books → Open Library (title only).

Each method returns a `DynamicImageResponse` with a `MemoryStream`. One image at a time, typically under 1 MB.

### Why Raw Stream Copy

Jellyfin's built-in Image Extractor uses ffmpeg to decode embedded artwork. This fails when the codec tag doesn't match the actual data — common in MP3 files where JPEG art is tagged as PNG in ID3. Jelly Covers uses `-vcodec copy` (no decoding) and identifies format from magic bytes.

### Backward Compatibility

Renamed from "Book Cover" at v5.0.0.0. Config page checks both provider names (`legacyName: 'Book Cover'`). When toggling a library, removes legacy name and adds new one. GUID unchanged.

### Online Cover Fetching

`OnlineCoverFetcher.ParseBookInfo()` cleans audiobook filenames — strips format tags `(Mp3)`, locale tags `[Castellano]`, Audible codes, year suffixes, series indicators. Extracts author from `(year, author)` pattern. Splits on last ` - ` for `Title - Author`.

Search order: Open Library (title + author) → Google Books (highest resolution) → Open Library (title only). No API keys. Static `HttpClient` with `SocketsHttpHandler`, 15s timeout. Images <1000 bytes rejected as placeholders.

## Configuration

Editable via **Dashboard → Jelly Covers** (sidebar entry via `EnableInMainMenu = true`).

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Dpi` | `int` | `150` | PDF rendering resolution |
| `JpegQuality` | `int` | `85` | PDF JPEG output quality (1–100) |
| `TimeoutSeconds` | `int` | `30` | Max time per pdftoppm/ffmpeg invocation |
| `EnableOnlineCoverFetch` | `bool` | `true` | Online fallback via Open Library and Google Books |

### Per-Library Toggle

Config page queries `Library/VirtualFolders`, shows each library's status. Enable/Disable buttons update `ImageFetchers` via `POST Library/VirtualFolders/LibraryOptions`. Refresh Images button triggers metadata rescan.

### API

| Method | Path | Auth | Returns |
|--------|------|------|---------|
| `GET` | `/JellyCovers/Status` | Admin (`RequiresElevation`) | `{ pdftoppmAvailable, ffmpegAvailable, onlineCoverFetchEnabled }` |

## Development Guidelines

### Build

```bash
dotnet build JellyCovers/JellyCovers.csproj -c Release
# Output: JellyCovers/bin/Release/net9.0/JellyCovers.dll
```

### Deploy

Copy DLL to `<jellyfin-config>/plugins/JellyCovers_<version>/` and restart Jellyfin. Or install from plugin catalog:

```
https://raw.githubusercontent.com/GeiserX/jelly-covers/main/manifest.json
```

### CI/CD

GitHub Actions (`.github/workflows/build.yml`):

1. **Build** (all pushes) — Restores, builds, packages `JellyCovers.dll` + `build.yaml` into `jelly-covers.zip`
2. **Release** (tag pushes) — Creates GitHub Release, generates `manifest.json` with checksum

Manifest auto-commit fails due to branch protection. After each release: download zip, `sha256sum`, update `manifest.json` manually, commit and push.

Version in `JellyCovers.csproj` (`<AssemblyVersion>` + `<FileVersion>`) must match `build.yaml`. Tags: `v5.0.0.0` format.

### Config Page

- Jellyfin custom elements: `emby-input`, `emby-button`, `emby-select`, `emby-checkbox`
- Standard CSS classes only (`inputContainer`, `selectContainer`, etc.) — no custom CSS
- All logic in `JellyCoversConfig` namespace object
- Embedded resource — changes require DLL rebuild

## Boundaries

### Always (do without asking)

- Read any file in the project
- Modify source files in `JellyCovers/`
- Run build commands
- Fix compiler warnings or errors
- Update documentation and README

### Ask First

- Add NuGet dependencies (affects DLL size and compatibility)
- Change the plugin GUID (breaks update path for existing users)
- Modify the CI/CD workflow
- Change supported item types (`Supports()` method)
- Add new API endpoints

### Never

- Commit secrets or API keys
- Force push to git
- Reuse existing version tags
- Remove backward compatibility for legacy "Book Cover" name (users may still have it configured)
- Use string concatenation for process arguments (command injection risk)

## Code Style

- Use C# conventions: PascalCase for public members, camelCase with underscore prefix for private fields
- Prefer `async/await` with `.ConfigureAwait(false)` throughout
- Guard external process calls with timeouts and cancellation tokens
- Use `Process.StartInfo.ArgumentList` (never string interpolation) for shell commands
- Clean up temp files in `finally` blocks using `Guid.NewGuid()` names
- Catch `Exception ex when (ex is not OperationCanceledException)` — never swallow cancellation
- Keep memory allocation minimal: one `MemoryStream` per extraction, no buffering across items

## Troubleshooting

| Problem | Cause | Fix |
|---------|-------|-----|
| No PDF covers | `pdftoppm` missing | `apt-get install poppler-utils` in container |
| No audio covers | `ffmpeg` missing | Check `which ffmpeg` inside container |
| Plugin doesn't run | Higher-priority provider already supplied cover | Delete existing cover, rescan library |
| No online covers | Disabled or no internet | Check toggle in settings; verify access to `openlibrary.org`, `googleapis.com` |
| Config page not in sidebar | `EnableInMainMenu` missing | Verify `Plugin.cs` returns it in `PluginPageInfo` |

## Learned Patterns

Things discovered during development that save time and prevent mistakes:

- **Sidebar visibility**: `EnableInMainMenu = true` on `PluginPageInfo` is the ONLY way to get a plugin config page into Jellyfin's dashboard sidebar. `MenuSection`, `MenuIcon`, and `DisplayName` properties do NOT exist — don't waste time trying them.
- **Config page styling**: NEVER use custom CSS. Jellyfin's built-in `emby-*` components and standard classes (`inputContainer`, `fieldDescription`, etc.) handle everything. Custom styling breaks across Jellyfin themes.
- **Library API**: `GET Library/VirtualFolders` returns libraries. Each has `LibraryOptions.TypeOptions[].ImageFetchers[]` — an array of enabled provider names. `POST Library/VirtualFolders/LibraryOptions` with the full `LibraryOptions` object updates them. You must send the complete object, not a partial update.
- **Provider name in library configs**: When users enabled "Book Cover" in their libraries, that string is stored in `ImageFetchers`. The config page must check for BOTH `"Jelly Covers"` and `"Book Cover"` when showing status, and replace the old name on toggle. Do not remove this backward compatibility.
- **Memory footprint**: The plugin processes one item at a time, returns one small `MemoryStream` per extraction (typically <1 MB). No caching, no buffering across items. It has been verified NOT to contribute to Jellyfin memory issues during library scans.
- **CI manifest workaround**: The `stefanzweifel/git-auto-commit-action` step in the release workflow always fails due to branch protection rules. This is expected. The manual steps (download zip → sha256sum → update manifest.json → push) are the permanent workflow.
- **Awesome-list PRs**: Open PRs exist at `awesome-jellyfin/awesome-jellyfin` and `quozd/awesome-dotnet` referencing this plugin. If the plugin is renamed again, those PRs need updating (branch content + PR title/body).
- **Deploy path on production**: The Jellyfin instance runs on `watchtower` (Unraid). Plugin path: `/mnt/user/appdata/arr/jellyfin/config/plugins/JellyCovers_<version>/`. Old plugin folders may have FUSE hidden files while Jellyfin is running — restart first, then delete.

## ⚠️ Security Notice

> **Do not commit secrets to the repository or to the live app.**
> Always use secure standards to transmit sensitive information.
> Use environment variables, secret managers, or secure vaults for credentials.

**🔍 Security Audit Recommendation:** When making changes that involve authentication, data handling, API endpoints, or dependencies, proactively offer to perform a security review of the affected code.

---

*Generated by [LynxPrompt](https://lynxprompt.com)*
