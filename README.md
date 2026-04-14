<p align="center">
  <img src="docs/images/banner.svg" alt="smart-covers banner" width="900"/>
</p>

<p align="center">
  <strong>Cover extraction for Jellyfin libraries with online fallback</strong>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/smart-covers/releases/latest"><img src="https://img.shields.io/github/v/release/GeiserX/smart-covers?style=flat-square&color=6B4C9A" alt="Latest Release"/></a>
  <a href="https://github.com/GeiserX/smart-covers/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/smart-covers/build.yml?branch=main&style=flat-square&label=tests" alt="Tests"/></a>
  <a href="https://github.com/GeiserX/smart-covers/actions"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/smart-covers/build.yml?branch=main&style=flat-square" alt="Build Status"/></a>
  <a href="https://github.com/GeiserX/smart-covers/blob/main/LICENSE"><img src="https://img.shields.io/github/license/GeiserX/smart-covers?style=flat-square&color=AA5CC3" alt="License"/></a>
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-6B4C9A?style=flat-square" alt="Jellyfin 10.11+"/>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square" alt=".NET 9.0"/>
  <a href="https://github.com/awesome-jellyfin/awesome-jellyfin#readme"><img src="https://img.shields.io/badge/listed%20on-awesome--jellyfin-00a4dc?style=flat-square&logo=jellyfin&logoColor=white" alt="listed on awesome-jellyfin"/></a>
</p>

---

A Jellyfin plugin that provides **cover-image extraction** for books, audiobooks, comics, magazines, and music libraries. It works alongside built-in providers as a safety net: when they fail to find a cover -- or crash on mislabeled embedded art -- SmartCovers steps in. As a final fallback, it can search **Open Library** and **Google Books** for cover images automatically.

## Supported Formats

| Format | Type | Extraction Method |
|--------|------|-------------------|
| PDF | Book / Magazine / Comic | First-page rendering via built-in PDFium (no external tools needed) |
| EPUB | Book | Archive introspection with 3-tier image search |
| MP3 | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| M4A / M4B | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| FLAC | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| OGG / Opus | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| WMA | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| AAC | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| WAV | Audiobook / Music | Embedded art via `ffmpeg` raw stream copy |
| Folder | Audiobook | Sidecar image lookup, then first-file embedded art |
| Any | All | Online fallback via Open Library and Google Books |

## How It Works

### PDF -- First Page Rendering

The plugin renders the first page of a PDF as a JPEG using a bundled PDFium native library (via the [PDFtoImage](https://www.nuget.org/packages/PDFtoImage) NuGet package). No external tools like `poppler-utils` or `pdftoppm` are required. DPI is configurable, and a per-render timeout prevents hangs on malformed files. The native library is included for Linux (x64, arm64, musl), macOS, and Windows.

### EPUB -- 3-Tier Archive Search

EPUBs are ZIP archives. When other plugins fail to extract a cover, SmartCovers opens the archive and searches with three strategies, in order:

1. **By filename** -- files explicitly named `cover`, `portada`, `front`, `frontcover`, or `book_cover` (with any image extension).
2. **By path** -- any image file with `cover` in its full archive path (e.g., `OEBPS/Images/cover-image.jpg`).
3. **By size** -- the largest image in the archive (above 5 KB, to skip icons and logos).

### Audio -- Raw Stream Copy with Magic-Byte Detection

Jellyfin's built-in Image Extractor uses ffmpeg to *decode* embedded artwork. This fails when the codec tag does not match the actual data -- a common problem in MP3 files where JPEG cover art is tagged as PNG in ID3 metadata.

SmartCovers sidesteps this entirely by using `ffmpeg -vcodec copy` to **raw-copy** the embedded image stream without decoding. It then identifies the actual format by inspecting magic bytes:

| Magic Bytes | Detected Format |
|-------------|-----------------|
| `FF D8 FF` | JPEG |
| `89 50 4E 47` | PNG |
| `47 49 46` | GIF |
| `52 49 46 46 ... 57 45 42 50` | WebP |

Any leading null/padding bytes injected by the raw stream copy are stripped automatically.

### Folder-Based Audiobooks

For multi-file audiobooks stored as a directory of chapter files, the plugin:

1. Checks for sidecar images in the folder (`cover.jpg`, `folder.jpg`, `front.jpg`, `poster.jpg`, `thumb.jpg`).
2. Falls back to extracting embedded art from the first audio file in the directory.

### Online Cover Fetching (Last Resort)

When all local extraction methods fail, the plugin can search online sources for a matching cover:

1. **Open Library** (openlibrary.org) -- searched first, using title and author metadata.
2. **Google Books** (books.google.com) -- searched as a fallback, preferring the highest-resolution image available.
3. If author-qualified search finds nothing, a title-only retry is attempted on Open Library.

The plugin parses clean titles and authors from item metadata, stripping common audiobook filename noise (format tags like `(Mp3)`, locale tags like `[Castellano]`, Audible codes, year suffixes, and series indicators). No API keys are required. Fetched covers are cached by Jellyfin after the first scan, so online lookups only happen once per item.

This feature is **enabled by default** and can be toggled in the plugin settings.

## Installation

### From Plugin Repository (Recommended)

Add the following repository URL in **Dashboard > Plugins > Repositories**:

```
https://geiserx.github.io/smart-covers/manifest.json
```

Then install **SmartCovers** from the plugin catalog and restart Jellyfin.

### From Releases

1. Download `smart-covers_7.0.0.0.zip` from the [latest release](https://github.com/GeiserX/smart-covers/releases/latest).
2. Extract the contents into your Jellyfin plugins directory:
   ```
   <jellyfin-config>/plugins/SmartCovers_7.0.0.0/
   ```
   The zip contains `SmartCovers.dll`, `PDFtoImage.dll`, and native PDFium libraries for all platforms under `runtimes/`.
3. Restart Jellyfin.

### Building from Source

```bash
dotnet publish SmartCovers/SmartCovers.csproj -c Release -o publish
```

The output will be in the `publish/` directory. Copy `SmartCovers.dll`, `PDFtoImage.dll`, and the `runtimes/` folder containing native PDFium libraries to your plugins directory.

## Requirements

| Dependency | Required For | Notes |
|------------|-------------|-------|
| Jellyfin 10.11+ | All features | Minimum supported server version |
| `ffmpeg` | Audio covers | Bundled with Jellyfin Docker images |
| [Bookshelf plugin](https://github.com/jellyfin/jellyfin-plugin-bookshelf) v13+ | EPUB covers | Recommended; handles standard EPUB covers as primary provider |

PDF rendering requires no external dependencies -- the native PDFium library is bundled with the plugin for all platforms (Linux x64/arm64, macOS, Windows).

## Configuration

After installation, configure the plugin in **Dashboard > SmartCovers** (appears in the sidebar):

| Setting | Default | Description |
|---------|---------|-------------|
| Online Cover Fetching | Enabled | Search Open Library and Google Books when local extraction fails. No API key needed. |
| DPI | 150 | Resolution for PDF first-page rendering. Higher values produce sharper covers at the cost of speed. |
| Timeout | 30 s | Maximum time allowed per extraction. Applies to both PDF rendering and `ffmpeg`. |

### Per-Library Enable/Disable

The plugin settings page includes a **Libraries** section where you can enable or disable SmartCovers for each library directly -- no need to navigate to individual library settings. A **Refresh Images** button is available for enabled libraries.

## Troubleshooting

**PDF covers are not extracted**
- Check the plugin config page -- it shows whether the PDFium native library loaded successfully.
- Check the Jellyfin log for `PDFium native library failed to load`.

**Audio covers are not extracted**
- Confirm `ffmpeg` is available: run `which ffmpeg` inside the container.
- Check the Jellyfin log for `ffmpeg not found`.

**Covers appear for some items but not others**
- The plugin only acts as a fallback. If a higher-priority provider already supplied a cover, this plugin will not run.
- To force re-extraction, delete the existing cover image for the item in Jellyfin and rescan the library.

**Extracted cover looks corrupted**
- This is rare but can happen if the embedded art stream contains unusual padding. Open an issue with the file format details and the Jellyfin log output.

**Online covers are not being fetched**
- Check that "Enable online cover fetching" is toggled on in the plugin settings.
- Verify the Jellyfin server has outbound internet access (the plugin queries `openlibrary.org` and `googleapis.com`).
- Items that already have a cover from a higher-priority provider will not trigger online fetching. Delete the existing cover and rescan to force it.

## Other Jellyfin Projects by GeiserX

- [quality-gate](https://github.com/GeiserX/quality-gate) — Restrict users to specific media versions based on configurable path-based policies
- [whisper-subs](https://github.com/GeiserX/whisper-subs) — Automatic subtitle generation using local AI models powered by whisper.cpp
- [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder) — Automatic 720p HEVC/AV1 transcoding service with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) — Sync Jellyfin access with Telegram channel membership

## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
