# Configuration

After installation, configure the plugin in **Dashboard > SmartCovers** (appears in the sidebar):

| Setting | Default | Description |
|---------|---------|-------------|
| Online Cover Fetching | Enabled | Search Open Library and Google Books when local extraction fails. No API key needed. |
| DPI | 150 | Resolution for PDF first-page rendering. Higher values produce sharper covers at the cost of speed. |
| Timeout | 30 s | Maximum time allowed per extraction. Applies to both PDF rendering and `ffmpeg`. |

## Per-Library Enable/Disable

The plugin settings page includes a **Libraries** section where you can enable or disable SmartCovers for each library directly -- no need to navigate to individual library settings. A **Refresh Images** button is available for enabled libraries.

The scheduled task that refreshes items missing a cover is described in [How it works](how-it-works.md#refreshing-covers-that-were-missed).
