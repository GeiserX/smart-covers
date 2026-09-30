<p align="center">
  <img src="docs/images/banner.svg" alt="smart-covers" width="900"/>
</p>

<p align="center">
  <strong>Every book, comic and audiobook gets a cover.</strong>
</p>

<p align="center">
  <a href="https://github.com/GeiserX/smart-covers/releases/latest"><img src="https://img.shields.io/github/v/release/GeiserX/smart-covers?style=flat-square&color=6B4C9A" alt="Latest release"/></a>
  <a href="https://github.com/GeiserX/smart-covers/actions/workflows/build.yml"><img src="https://img.shields.io/github/actions/workflow/status/GeiserX/smart-covers/build.yml?branch=main&style=flat-square&label=tests" alt="Tests"/></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/GeiserX/smart-covers?style=flat-square&color=AA5CC3" alt="License"/></a>
  <a href="https://github.com/GeiserX/smart-covers/releases"><img src="https://img.shields.io/github/downloads/GeiserX/smart-covers/total?style=flat-square&color=6B4C9A" alt="Downloads"/></a>
  <a href="https://github.com/awesome-jellyfin/awesome-jellyfin#readme"><img src="https://img.shields.io/badge/listed%20on-awesome--jellyfin-00a4dc?style=flat-square&logo=jellyfin&logoColor=white" alt="Listed on awesome-jellyfin"/></a>
</p>

---

**smart-covers** is a Jellyfin plugin that gives a cover to the books, comics and audiobooks Jellyfin leaves blank. It takes the cover from the file itself (the first page of a PDF or comic, the cover image inside an EPUB or MOBI, the art embedded in an audio file), and when the file has none it asks Open Library and then Google Books, with no API key. It runs on Jellyfin 10.11 or newer and appears in the catalog and the dashboard as **SmartCovers**.

<p align="center"><img src="docs/images/screenshots/library-before-after.png" alt="The same Jellyfin Books library twice: fifteen blank tiles before SmartCovers, and the same fifteen tiles each with a cover after it, from PDF first pages, EPUB and MOBI cover images and Open Library" width="900"></p>

## Features

- PDFs get a cover from their first page, rendered by a bundled PDFium; nothing to install on the server.
- EPUBs whose cover Jellyfin or Bookshelf misses still get one: the archive is searched by file name, then by path, then by the largest image.
- MOBI and AZW books get the cover stored in the file.
- CBZ and CBR comics show their first page in natural page order, RAR4, RAR5 and solid archives included; a RAR renamed to `.cbz` still works.
- Audiobooks and music (MP3, M4A/M4B, FLAC, OGG/Opus, WMA, AAC, WAV) show their embedded art, including art tagged with the wrong image format that Jellyfin's own extractor drops.
- A folder audiobook gets one cover for the book: a sidecar image if there is one, else the first track's art, multi-disc rips included.
- A file with no cover inside gets one from Open Library, then Google Books.
- Anything missed at scan time is retried by a daily task, and the settings page turns the plugin on and refreshes images one library at a time.

## Quick start

1. In **Dashboard > Plugins > Repositories**, add this URL, then install **SmartCovers** from the catalog and restart Jellyfin (10.11 or newer):

   ```
   https://geiserx.github.io/smart-covers/manifest.json
   ```

2. Open **Dashboard > SmartCovers**, click **Enable** on each library you want covered, then **Refresh Images**. The plugin is off on every library until you do this, including libraries you create later.

It worked when the SmartCovers page shows three green lines (PDF rendering, ffmpeg, online fetching) and covers appear on the tiles as the refresh runs. Whatever is still blank afterwards is retried every night at 04:00 by the **Refresh items missing a cover** task. The release zip and building from source are in [Getting started](docs/getting-started.md).

## Documentation

- [Getting started](docs/getting-started.md): catalog install, the release zip, building from source, requirements, turning it on, checking that it works
- [Configuration](docs/configuration.md): the settings page, every setting and its default, per-library enable and refresh
- [How it works](docs/how-it-works.md): how each format is read, the online lookup, the daily task
- [Troubleshooting](docs/troubleshooting.md): installed but nothing changed, covers missing for one format, reporting a bug

## Related projects

- [quality-gate](https://github.com/GeiserX/quality-gate): restrict users to specific media versions with path-based policies
- [quality-gate-encoder](https://github.com/GeiserX/quality-gate-encoder): automatic 720p HEVC, H.264 or AV1 versions of a library, with NVIDIA and Intel hardware acceleration
- [whisper-subs](https://github.com/GeiserX/whisper-subs): subtitles generated on your own server with local Whisper models
- [jellyfin-telegram-channel-sync](https://github.com/GeiserX/jellyfin-telegram-channel-sync): Jellyfin user access that follows Telegram channel membership

## License

[GPL-3.0-or-later](LICENSE). Bundled third-party components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
