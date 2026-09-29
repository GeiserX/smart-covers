# Troubleshooting

**PDF covers are not extracted**
- Check the plugin config page -- it shows whether the PDFium native library loaded successfully.
- Grep the Jellyfin log for `pdfium native library`. On success it logs `pdfium native library loaded — PDF cover extraction enabled`; if the bundled native cannot load on this host it logs `pdfium native library not available — PDF cover extraction disabled` and PDF cover extraction is disabled while all other features keep working.

**Audio covers are not extracted**
- Confirm `ffmpeg` is available: run `which ffmpeg` inside the container.
- Check the Jellyfin log for `ffmpeg not found`.

**Comic (CBZ/CBR) covers are not extracted**
- Grep the Jellyfin log for `Failed to extract comic cover` -- corrupt archives are skipped (the online fallback still applies).
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
