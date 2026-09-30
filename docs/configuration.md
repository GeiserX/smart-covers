# Configuration

After installation, configure the plugin in **Dashboard > SmartCovers** (appears in the sidebar):

| Setting | Default | Description |
|---------|---------|-------------|
| Online Cover Fetching | Enabled | Search Open Library and Google Books when local extraction fails. No API key needed. |
| DPI | 150 | Resolution for PDF first-page rendering. Higher values produce sharper covers at the cost of speed. |
| Timeout | 30 s | Maximum time allowed per extraction. Applies to both PDF rendering and `ffmpeg`. |
| JPEG quality | 85 | Quality of the rendered PDF cover. Config file only (`JpegQuality` in `plugins/configurations/SmartCovers.xml` under the Jellyfin config directory); not on the settings page. |

## Per-library enable and refresh

The plugin is off on every library until it is enabled, including libraries created after the install (Jellyfin
only pre-selects its own built-in fetchers). The **Libraries** table on the settings page lists every Books,
Audiobooks, Music and Mixed library with an **Enable** or **Disable** button; **Refresh Images** on an enabled
library runs a full image refresh of that library without touching metadata or replacing images it already has.
Enable writes `SmartCovers` into the library's image fetchers for the item types it holds (books and audiobooks in a
Books library, tracks and albums in a Music library), which is the same switch as the library's own settings page.

The scheduled task that refreshes items missing a cover is described in [How it works](how-it-works.md#refreshing-covers-that-were-missed).
