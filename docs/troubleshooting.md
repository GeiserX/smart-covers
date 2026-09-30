# Troubleshooting

**Installed and restarted, but nothing changed**
- The plugin is off until you enable it per library. Open **Dashboard > SmartCovers**, click **Enable** on the
  library, then **Refresh Images**. The SmartCovers row must also be ticked under the library's own Image
  fetchers if you manage those by hand.
- Items scanned before the plugin was enabled keep their blank tile until an image refresh or the nightly
  **Refresh items missing a cover** task reaches them.

**PDF covers are not extracted**
- Check the plugin config page: it shows whether the PDFium native library loaded successfully.
- Grep the Jellyfin log for `pdfium native library`. On success it logs `pdfium native library loaded — PDF cover extraction enabled`; if the bundled native cannot load on this host it logs `pdfium native library not available — PDF cover extraction disabled` and PDF cover extraction is disabled while all other features keep working.

**Audio covers are not extracted**
- Confirm `ffmpeg` is available: run `which ffmpeg` inside the container.
- Check the Jellyfin log for `ffmpeg not found`.

**Comic (CBZ/CBR) covers are not extracted**
- Grep the Jellyfin log for `Failed to extract comic cover`. Corrupt archives are skipped (the online fallback still applies).
- Archives whose images are all smaller than 1 KB, or that contain no image entries at all, produce no cover by design.

**Covers appear for some items but not others**
- The plugin only acts as a fallback. If a higher-priority provider already supplied a cover, this plugin will not run.
- To force re-extraction, delete the existing cover image for the item in Jellyfin and rescan the library.

**Extracted cover looks corrupted**
- This is rare but can happen if the embedded art stream contains unusual padding. Open an issue with the file format details and the Jellyfin log output.

**Online covers are not being fetched**
- Check that "Enable online cover fetching" is toggled on in the plugin settings.
- Verify the Jellyfin server has outbound internet access (the plugin queries `openlibrary.org` and `googleapis.com`).
- Items that already have a cover from a higher-priority provider will not trigger online fetching. Delete the existing cover and rescan to force it.

## Reporting a bug

Open an [issue](https://github.com/GeiserX/smart-covers/issues) with: the Jellyfin version and how it runs
(Docker image tag, package, OS), the plugin version from Dashboard > Plugins, the file's format and how it is
stored (single file, folder, multi-disc), the three status lines from the SmartCovers page, and the Jellyfin log
lines that mention `SmartCovers` around the scan. Do not attach the book itself unless it is public domain.
