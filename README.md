<p align="center">
  <img src="docs/images/banner.svg" alt="smart-covers banner" width="900"/>
</p>

<p align="center">
  <strong>Cover extraction for Jellyfin libraries with online fallback</strong>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/smart-covers/releases/latest"><img src="https://img.shields.io/github/v/release/GeiserX/smart-covers?style=flat-square&color=6B4C9A" alt="Latest Release"/></a>
  <a href="https://github.com/GeiserX/smart-covers/actions"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/smart-covers/build.yml?branch=main&style=flat-square" alt="Build Status"/></a>
  <a href="https://github.com/GeiserX/smart-covers/blob/main/LICENSE"><img src="https://img.shields.io/github/license/GeiserX/smart-covers?style=flat-square&color=AA5CC3" alt="License"/></a>
  <img src="https://img.shields.io/badge/Jellyfin-10.11%2B-6B4C9A?style=flat-square" alt="Jellyfin 10.11+"/>
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square" alt=".NET 9.0"/>
</p>

---

A Jellyfin plugin that provides **cover-image extraction** for books, audiobooks, comics, magazines, and music libraries. It works alongside built-in providers as a safety net: when they fail to find a cover -- or crash on mislabeled embedded art -- SmartCovers steps in. As a final fallback, it can search **Open Library** and **Google Books** for cover images automatically.

## Supported Formats

| Format | Type | Extraction Method |
|--------|------|-------------------|
| PDF | Book / Magazine / Comic | First-page rendering via `pdftoppm` (poppler-utils) |
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

The plugin shells out to `pdftoppm` (from poppler-utils) to render the first page of a PDF as a JPEG. DPI and JPEG quality are configurable. A per-process timeout prevents hangs on malformed files.

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

### From Releases

1. Download `smart-covers.zip` from the [latest release](https://github.com/GeiserX/smart-covers/releases/latest).
2. Extract `SmartCovers.dll` into your Jellyfin plugins directory:
   ```
   <jellyfin-config>/plugins/SmartCovers_6.0.0.0/SmartCovers.dll
   ```
3. Restart Jellyfin.

### From Plugin Repository

Add the following repository URL in **Dashboard > Plugins > Repositories**:

```
https://raw.githubusercontent.com/GeiserX/smart-covers/main/manifest.json
```

Then install **SmartCovers** from the plugin catalog and restart Jellyfin.

### Building from Source

```bash
dotnet build SmartCovers/SmartCovers.csproj -c Release
```

The compiled DLL will be at:
```
SmartCovers/bin/Release/net9.0/SmartCovers.dll
```

## Requirements

| Dependency | Required For | Notes |
|------------|-------------|-------|
| Jellyfin 10.11+ | All features | Minimum supported server version |
| `poppler-utils` | PDF covers | Provides `pdftoppm`; not bundled with Jellyfin |
| `ffmpeg` | Audio covers | Bundled with Jellyfin Docker images |
| [Bookshelf plugin](https://github.com/jellyfin/jellyfin-plugin-bookshelf) v13+ | EPUB covers | Recommended; handles standard EPUB covers as primary provider |

### Installing poppler-utils (Docker)

Add this to your `docker-compose.yml` entrypoint so `pdftoppm` is available at runtime:

```yaml
entrypoint:
  - /bin/bash
  - -c
  - |
    which pdftoppm > /dev/null 2>&1 || \
      (apt-get update -qq && \
       apt-get install -y -qq --no-install-recommends poppler-utils > /dev/null 2>&1 && \
       rm -rf /var/lib/apt/lists/*)
    exec /jellyfin/jellyfin
```

## Configuration

After installation, configure the plugin in **Dashboard > SmartCovers** (appears in the sidebar):

| Setting | Default | Description |
|---------|---------|-------------|
| Online Cover Fetching | Enabled | Search Open Library and Google Books when local extraction fails. No API key needed. |
| DPI | 150 | Resolution for PDF first-page rendering. Higher values produce sharper covers at the cost of speed. |
| JPEG Quality | 85 | Output compression level (1--100). Lower values produce smaller files. |
| Timeout | 30 s | Maximum time allowed per extraction. Applies to both `pdftoppm` and `ffmpeg`. |

### Per-Library Enable/Disable

The plugin settings page includes a **Libraries** section where you can enable or disable SmartCovers for each library directly -- no need to navigate to individual library settings. A **Refresh Images** button is available for enabled libraries.

## Troubleshooting

**PDF covers are not extracted**
- Verify that `pdftoppm` is installed and accessible: run `which pdftoppm` inside the Jellyfin container.
- Check the Jellyfin log for `pdftoppm not found`.

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
- [whisper-subs](https://github.com/GeiserX/whisper-subs) — Automatically generates subtitles using local AI models powered by Whisper
- [jellyfin-encoder](https://github.com/GeiserX/jellyfin-encoder) — Automatic 720p HEVC/AV1 transcoding service
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) — Sync Jellyfin access with Telegram channel membership


## License

This project is licensed under the [GNU General Public License v3.0](LICENSE).
