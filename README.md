<p align="center">
  <img src="docs/images/banner.svg" alt="smart-covers" width="900"/>
</p>

<p align="center">
  <strong>Cover extraction for Jellyfin libraries with online fallback</strong>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/smart-covers/releases/latest"><img src="https://img.shields.io/github/v/release/GeiserX/smart-covers?style=flat-square&color=6B4C9A" alt="Latest Release"/></a>
  <a href="https://github.com/GeiserX/smart-covers/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/smart-covers/build.yml?branch=main&style=flat-square&label=tests" alt="Tests"/></a>
  <a href="https://github.com/GeiserX/smart-covers/blob/main/LICENSE"><img src="https://img.shields.io/github/license/GeiserX/smart-covers?style=flat-square&color=AA5CC3" alt="License"/></a>
  <a href="https://github.com/awesome-jellyfin/awesome-jellyfin#readme"><img src="https://img.shields.io/badge/listed%20on-awesome--jellyfin-00a4dc?style=flat-square&logo=jellyfin&logoColor=white" alt="listed on awesome-jellyfin"/></a>
  <a href="https://codecov.io/gh/GeiserX/smart-covers"><img src="https://codecov.io/gh/GeiserX/smart-covers/graph/badge.svg" alt="codecov"/></a>
</p>

---

A Jellyfin plugin that provides **cover-image extraction** for books, audiobooks, comics, magazines, and music libraries. It works alongside built-in providers as a safety net: when they fail to find a cover -- or crash on mislabeled embedded art -- SmartCovers steps in. As a final fallback, it can search **Open Library** and **Google Books** for cover images automatically.

## Features

- **PDF**: first-page rendering with a bundled PDFium library, no external tools.
- **EPUB**: a 3-tier archive search (by filename, by path, by size).
- **CBZ / CBR**: the first page in natural order, detected by content rather than extension, RAR4 and RAR5 included.
- **Audio** (MP3, M4A/M4B, FLAC, OGG/Opus, WMA, AAC, WAV): embedded art by raw `ffmpeg` stream copy and magic-byte detection, so mislabeled art still works.
- **Folder audiobooks**: sidecar images first, then the first file's embedded art, including multi-disc rips.
- **Online fallback**: Open Library, then Google Books, with no API key.
- A scheduled task that refreshes items still missing a cover.
- Per-library enable/disable from the plugin settings page.

## Quick start

Add this repository in **Dashboard > Plugins > Repositories**, install **SmartCovers** from the catalog, and restart Jellyfin (10.11 or newer):

```
https://geiserx.github.io/smart-covers/manifest.json
```

## Documentation

- [Getting started](docs/getting-started.md): plugin repository, release zip, building from source, requirements
- [Configuration](docs/configuration.md): settings and per-library enable/disable
- [How it works](docs/how-it-works.md): supported formats and the extraction method for each, online fetching, the refresh task
- [Troubleshooting](docs/troubleshooting.md)

## Related projects

- [quality-gate](https://github.com/GeiserX/quality-gate) — Restrict users to specific media versions based on configurable path-based policies
- [whisper-subs](https://github.com/GeiserX/whisper-subs) — Automatic subtitle generation using local AI models powered by whisper.cpp
- [quality-gate-encoder](https://github.com/GeiserX/quality-gate-encoder) (formerly jellyfin-encoder) — Automatic 720p HEVC, H.264 or AV1 copies with hardware acceleration
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync) — Sync Jellyfin access with Telegram channel membership

## License

[GPL-3.0-or-later](LICENSE). Bundled third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
