using System.Globalization;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
using SmartCovers.Configuration;

namespace SmartCovers;

/// <summary>
/// Refreshes the images of library items that still have no cover.
///
/// Jellyfin runs image providers when an item is new and does not come back to a
/// miss. Anything that was scanned before SmartCovers was installed — or while a
/// dependency was failing to load, or while an online catalogue was rate-limited —
/// therefore stays blank forever, however well the plugin works afterwards. This
/// task closes that gap by asking Jellyfin to try those items again. Items that
/// carry a drawn cover are asked again too, so a real cover replaces it once one
/// exists.
/// </summary>
public class RefreshMissingCoversTask : IScheduledTask, IConfigurableScheduledTask
{
    // Quiet hour by default: this can do online lookups and PDF rendering.
    private static readonly TimeSpan DefaultRunTime = TimeSpan.FromHours(4);

    private readonly ILogger<RefreshMissingCoversTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IDirectoryService _directoryService;
    private readonly CoverOutcomeLog _outcomes;

    /// <summary>
    /// Initializes a new instance of the <see cref="RefreshMissingCoversTask"/> class.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public RefreshMissingCoversTask(
        ILogger<RefreshMissingCoversTask> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IDirectoryService directoryService,
        CoverOutcomeLog outcomes)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _directoryService = directoryService;
        _outcomes = outcomes;
    }

    /// <summary>
    /// Where the plugin settings come from. Tests replace it.
    /// </summary>
    internal Func<PluginConfiguration?> ConfigurationSource { get; set; } = static () => Plugin.Instance?.Configuration;

    private bool GeneratedCoversEnabled => ConfigurationSource()?.EnableGeneratedCovers == true;

    /// <inheritdoc />
    public string Name => "Refresh items missing a cover";

    /// <inheritdoc />
    public string Key => "SmartCoversRefreshMissingCovers";

    /// <inheritdoc />
    public string Description =>
        "Finds books, audiobooks and audiobook folders that still have no primary image "
        + "(or only a drawn one) in libraries where SmartCovers is an enabled image fetcher, "
        + "and refreshes their images. Jellyfin only tries once when an item is new, so "
        + "without this a cover that was missed stays missing.";

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

    /// <summary>
    /// The item types the task looks at. Music tracks only when drawn covers are on:
    /// a drawn cover is the only thing the plugin can give them.
    /// </summary>
    internal BaseItemKind[] CandidateTypes() =>
        GeneratedCoversEnabled
            ? [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder, BaseItemKind.Audio]
            : [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var options = CreateRefreshOptions();
        var replaceGenerated = CreateRefreshOptions(replacePrimary: true);

        var candidates = _libraryManager
            .GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = CandidateTypes(),
                Recursive = true
            })
            .Where(item => IsCandidate(item, options))
            .Select(item => Describe(item, LibraryContext.RootPath(item, _libraryManager)))
            // A book's tracks one after another, so the provider's per-book cache is
            // used instead of looking the same book up again for each track.
            .OrderBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var generatedBefore = candidates.Count(c => c.HadGeneratedCover);
        _logger.LogInformation(
            "Found {Total} items with no real cover: {Books} books, {Tracks} tracks in {Audiobooks} audiobooks, "
            + "{Files} single-file audiobooks, {Folders} audiobook folders, {Music} music tracks; "
            + "{Generated} of them carry a drawn cover and are checked for a real one",
            candidates.Count,
            candidates.Count(c => c.Kind == CandidateKind.Book),
            candidates.Count(c => c.Kind == CandidateKind.Track),
            candidates.Where(c => c.Kind == CandidateKind.Track).Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            candidates.Count(c => c.Kind == CandidateKind.AudiobookFile),
            candidates.Count(c => c.Kind == CandidateKind.Folder),
            candidates.Count(c => c.Kind == CandidateKind.Music),
            generatedBefore);

        if (candidates.Count == 0)
        {
            progress.Report(100);
            return;
        }

        _outcomes.Clear();
        var results = new List<(Candidate Candidate, Result Result)>(candidates.Count);

        for (var i = 0; i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = candidates[i];

            try
            {
                await _providerManager
                    .RefreshSingleItem(candidate.Item, candidate.HadGeneratedCover ? replaceGenerated : options, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable file must not abandon the rest of the run.
                _logger.LogDebug(ex, "Refresh failed for an item with no cover");
            }

            results.Add((candidate, Classify(candidate)));
            progress.Report((i + 1) * 100.0 / candidates.Count);
        }

        Report(results);
    }

    /// <summary>
    /// What became of one candidate, read from the item as it is now (fetched again,
    /// not the stale object the query returned) and from the provider's outcome.
    /// </summary>
    private Result Classify(Candidate candidate)
    {
        _outcomes.TryTake(candidate.Item.Id, out var outcome);

        BaseItem current = candidate.Item;
        try
        {
            current = _libraryManager.GetItemById(candidate.Item.Id) ?? candidate.Item;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not re-read an item after its refresh");
        }

        var hasImage = current.HasImage(ImageType.Primary, 0);
        var generatedNow = hasImage && GeneratedCover.IsGeneratedFile(PrimaryPath(current));

        Outcome state;
        if (hasImage && !generatedNow)
        {
            state = candidate.HadGeneratedCover ? Outcome.Upgraded : Outcome.Real;
        }
        else if (generatedNow)
        {
            state = candidate.HadGeneratedCover ? Outcome.KeptGenerated : Outcome.Generated;
        }
        else
        {
            state = Outcome.Missing;
        }

        return new Result(state, outcome);
    }

    private void Report(List<(Candidate Candidate, Result Result)> results)
    {
        foreach (var group in results.GroupBy(r => (r.Candidate.Kind, r.Candidate.Group, r.Result.State)))
        {
            var first = group.First();
            var outcome = group.Select(r => r.Result.Outcome).FirstOrDefault(o => o != null);
            var title = outcome?.Title ?? first.Candidate.Item.Name;
            var author = outcome?.Author ?? "unknown";
            var count = CountLabel(first.Candidate.Kind, group.Count());

            switch (group.Key.State)
            {
                case Outcome.Missing:
                    _logger.LogInformation(
                        "No cover for \"{Title}\" by \"{Author}\" ({Kind}): {Reason} [{Path}]",
                        title,
                        author,
                        count,
                        ReasonText(outcome),
                        first.Candidate.Group);
                    break;
                case Outcome.Generated:
                    _logger.LogInformation("Drew a cover for \"{Title}\" by \"{Author}\" ({Kind})", title, author, count);
                    break;
                case Outcome.Upgraded:
                    _logger.LogInformation(
                        "Replaced the drawn cover of \"{Title}\" with a real one ({Source})",
                        title,
                        outcome?.Source ?? ReasonText(outcome));
                    break;
            }
        }

        var real = results.Where(r => r.Result.State is Outcome.Real).ToList();
        var missing = results.Where(r => r.Result.State is Outcome.Missing).ToList();

        _logger.LogInformation(
            "Done: {Real} got a real cover ({Books} books, {Tracks} audiobook tracks, {Folders} audiobook folders, {Files} single-file audiobooks, {Music} music tracks), "
            + "{Generated} got a drawn cover, {Upgraded} drawn covers replaced with a real one, {Kept} keep their drawn cover, {Missing} still have none",
            real.Count,
            real.Count(r => r.Candidate.Kind == CandidateKind.Book),
            real.Count(r => r.Candidate.Kind == CandidateKind.Track),
            real.Count(r => r.Candidate.Kind == CandidateKind.Folder),
            real.Count(r => r.Candidate.Kind == CandidateKind.AudiobookFile),
            real.Count(r => r.Candidate.Kind == CandidateKind.Music),
            results.Count(r => r.Result.State is Outcome.Generated),
            results.Count(r => r.Result.State is Outcome.Upgraded),
            results.Count(r => r.Result.State is Outcome.KeptGenerated),
            missing.Count);

        if (missing.Count > 0)
        {
            var byReason = missing
                .GroupBy(r => ReasonKey(r.Result.Outcome))
                .OrderByDescending(g => g.Count())
                .Select(g => string.Create(CultureInfo.InvariantCulture, $"{g.Key} {g.Count()}"));
            _logger.LogInformation("Still without a cover, by reason: {Reasons}", string.Join(", ", byReason));
        }
    }

    internal static string CountLabel(CandidateKind kind, int count) => kind switch
    {
        CandidateKind.Book => count == 1 ? "book" : $"{count} books",
        CandidateKind.Track => $"audiobook, {count} tracks",
        CandidateKind.AudiobookFile => "single-file audiobook",
        CandidateKind.Folder => "audiobook folder",
        _ => count == 1 ? "music track" : $"{count} music tracks"
    };

    internal static string ReasonKey(CoverOutcome? outcome) => outcome?.Reason switch
    {
        null => "provider not run",
        CoverReason.NoImageInFile => "no image in file",
        CoverReason.NoOnlineMatch => "no online match",
        CoverReason.UnsearchableTitle => "unsearchable title",
        CoverReason.NetworkError => "network error",
        CoverReason.OnlineDisabled => "online lookup off",
        CoverReason.NotOneBook => "not one book",
        CoverReason.NotBookLibrary => "not a book library",
        CoverReason.NoFont => "no font to draw a cover",
        CoverReason.KeptGenerated => "kept the drawn cover",
        _ => "cover not saved"
    };

    private static string ReasonText(CoverOutcome? outcome)
    {
        var text = new StringBuilder(ReasonKey(outcome));
        if (!string.IsNullOrEmpty(outcome?.Detail))
        {
            text.Append(" (").Append(outcome.Detail).Append(')');
        }

        return text.ToString();
    }

    /// <summary>
    /// The refresh options: images only, keeping any image the item already has,
    /// or, with <paramref name="replacePrimary"/>, letting a real cover replace a
    /// drawn one.
    /// </summary>
    internal MetadataRefreshOptions CreateRefreshOptions(bool replacePrimary = false) =>
        new(_directoryService)
        {
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            MetadataRefreshMode = MetadataRefreshMode.None,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
            ReplaceImages = replacePrimary ? [ImageType.Primary] : []
        };

    /// <summary>
    /// Whether this item is worth refreshing: it has no cover (or only a drawn one),
    /// it is a shape SmartCovers handles, and SmartCovers is an enabled image fetcher
    /// for the library it lives in.
    /// </summary>
    internal bool IsCandidate(BaseItem item, ImageRefreshOptions options)
    {
        if (item.HasImage(ImageType.Primary, 0) && !GeneratedCover.IsGeneratedFile(PrimaryPath(item)))
        {
            return false;
        }

        // Folders are only worth refreshing when they stand for one book, in a book
        // library. The provider's Supports() is type-only, and Jellyfin enables it for
        // folders in every library, so without this the task would queue every movie
        // folder and every shelf just to have the provider decline each one.
        if (item.GetType() == typeof(Folder)
            && !(item.Path is { Length: > 0 } path
                && CoverImageProvider.RepresentsOneBook(path)
                && LibraryContext.IsBookLibrary(item, _libraryManager)))
        {
            return false;
        }

        // A music track can only get a drawn cover, and only when it sits loose in
        // the library's top folder: tracks in an album show the album's art.
        if (item is Audio and not AudioBook
            && !(GeneratedCoversEnabled && IsLooseInLibraryRoot(item)))
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

    private bool IsLooseInLibraryRoot(BaseItem item)
        => !string.IsNullOrEmpty(item.Path)
            && LibraryContext.IsSameDirectory(Path.GetDirectoryName(item.Path), LibraryContext.RootPath(item, _libraryManager));

    private static string? PrimaryPath(BaseItem item)
    {
        try
        {
            return item.GetImageInfo(ImageType.Primary, 0)?.Path;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sorts a candidate into the kind the report counts and the book it belongs to.
    /// </summary>
    internal static Candidate Describe(BaseItem item, string? libraryRoot)
    {
        var hadGenerated = item.HasImage(ImageType.Primary, 0) && GeneratedCover.IsGeneratedFile(PrimaryPath(item));
        var path = item.Path ?? item.Name ?? string.Empty;

        switch (item)
        {
            case Book:
                return new Candidate(item, CandidateKind.Book, path, hadGenerated);
            case AudioBook:
                var identity = string.IsNullOrEmpty(item.Path)
                    ? null
                    : BookIdentity.ResolveIdentityDirectory(Path.GetDirectoryName(item.Path), libraryRoot);
                return identity != null
                    ? new Candidate(item, CandidateKind.Track, identity, hadGenerated)
                    : new Candidate(item, CandidateKind.AudiobookFile, path, hadGenerated);
            case Audio:
                return new Candidate(item, CandidateKind.Music, path, hadGenerated);
            default:
                return new Candidate(item, CandidateKind.Folder, path, hadGenerated);
        }
    }

    /// <summary>What a candidate is, for counting.</summary>
    internal enum CandidateKind
    {
        /// <summary>An e-book, comic or PDF.</summary>
        Book,

        /// <summary>One track of a folder audiobook.</summary>
        Track,

        /// <summary>An audiobook that is a single file.</summary>
        AudiobookFile,

        /// <summary>The folder of an audiobook.</summary>
        Folder,

        /// <summary>A music track.</summary>
        Music
    }

    private enum Outcome
    {
        Real,
        Generated,
        Upgraded,
        KeptGenerated,
        Missing
    }

    /// <summary>A candidate with its kind and the book it belongs to (folder or file path).</summary>
    internal sealed record Candidate(BaseItem Item, CandidateKind Kind, string Group, bool HadGeneratedCover);

    private sealed record Result(Outcome State, CoverOutcome? Outcome);
}
