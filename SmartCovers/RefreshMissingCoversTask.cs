using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;

namespace SmartCovers;

/// <summary>
/// Refreshes the images of library items that still have no cover.
///
/// Jellyfin runs image providers when an item is new and does not come back to a
/// miss. Anything that was scanned before SmartCovers was installed — or while a
/// dependency was failing to load, or while an online catalogue was rate-limited —
/// therefore stays blank forever, however well the plugin works afterwards. This
/// task closes that gap by asking Jellyfin to try those items again.
/// </summary>
public class RefreshMissingCoversTask : IScheduledTask, IConfigurableScheduledTask
{
    // Quiet hour by default: this can do online lookups and PDF rendering.
    private static readonly TimeSpan DefaultRunTime = TimeSpan.FromHours(4);

    private readonly ILogger<RefreshMissingCoversTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshMissingCoversTask"/> class.
    /// </summary>
    public RefreshMissingCoversTask(
        ILogger<RefreshMissingCoversTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
    }

    /// <inheritdoc />
    public string Name => "Refresh items missing a cover";

    /// <inheritdoc />
    public string Key => "SmartCoversRefreshMissingCovers";

    /// <inheritdoc />
    public string Description =>
        "Finds books, audiobooks and audiobook folders that still have no primary image "
        + "in libraries where SmartCovers is an enabled image fetcher, and refreshes their "
        + "images. Jellyfin only tries once when an item is new, so without this a cover "
        + "that was missed stays missing.";

    /// <inheritdoc />
    public string Category => "SmartCovers";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = DefaultRunTime.Ticks
        }
    ];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = CreateRefreshOptions();

        var candidates = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder],
                Recursive = true
            })
            .Where(item => IsCandidate(item, options))
            .ToList();

        _logger.LogInformation("Found {Count} items with no cover to refresh", candidates.Count);

        if (candidates.Count == 0)
        {
            progress.Report(100);
            return;
        }

        var refreshed = 0;
        var failed = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _providerManager
                    .RefreshSingleItem(candidates[i], options, cancellationToken)
                    .ConfigureAwait(false);
                refreshed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable file must not abandon the rest of the run.
                failed++;
                _logger.LogDebug(ex, "Refresh failed for an item with no cover");
            }

            progress.Report((i + 1) * 100.0 / candidates.Count);
        }

        _logger.LogInformation(
            "Refreshed {Refreshed} items, {Failed} failed, {Covered} now have a cover",
            refreshed,
            failed,
            candidates.Count(item => item.HasImage(ImageType.Primary, 0)));
    }

    /// <summary>
    /// The refresh options: images only, keeping any image the item already has.
    /// </summary>
    internal MetadataRefreshOptions CreateRefreshOptions() =>
        new(_directoryService)
        {
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            MetadataRefreshMode = MetadataRefreshMode.None,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false
        };

    /// <summary>
    /// Whether this item is worth refreshing: it has no cover, it is a shape
    /// SmartCovers handles, and SmartCovers is an enabled image fetcher for the
    /// library it lives in.
    /// </summary>
    internal bool IsCandidate(BaseItem item, ImageRefreshOptions options)
    {
        if (item.HasImage(ImageType.Primary, 0))
        {
            return false;
        }

        // Folders are only worth refreshing when they stand for one book. The
        // provider's Supports() is type-only, so without this the task would queue
        // every shelf in the library just to have the provider decline each one.
        if (item.GetType() == typeof(Folder)
            && !(item.Path is { Length: > 0 } path && CoverImageProvider.RepresentsOneBook(path)))
        {
            return false;
        }

        // Ask Jellyfin itself whether we would run. This resolves the library's
        // ImageFetchers, and its fall back to the global metadata options, exactly
        // as a real refresh would — no second copy of that rule to drift.
        try
        {
            return _providerManager
                .GetImageProviders(item, options)
                .Any(provider => string.Equals(
                    provider.Name,
                    CoverImageProvider.ProviderName,
                    StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not resolve image providers for an item");
            return false;
        }
    }
}
