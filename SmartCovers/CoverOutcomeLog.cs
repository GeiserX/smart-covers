using System.Collections.Concurrent;

namespace SmartCovers;

/// <summary>
/// How a cover request ended.
/// </summary>
internal enum CoverReason
{
    /// <summary>An image inside or beside the file.</summary>
    FoundInFile,

    /// <summary>The book folder's own Jellyfin poster.</summary>
    InheritedFromFolder,

    /// <summary>An online catalogue had it.</summary>
    FoundOnline,

    /// <summary>A plain title and author cover was drawn.</summary>
    Generated,

    /// <summary>Nothing real was found and the item keeps the cover drawn earlier.</summary>
    KeptGenerated,

    /// <summary>No image in or beside the file, and no online lookup applies.</summary>
    NoImageInFile,

    /// <summary>The catalogues answered and had no cover.</summary>
    NoOnlineMatch,

    /// <summary>The name gives nothing a catalogue can search for ("01", "1").</summary>
    UnsearchableTitle,

    /// <summary>A catalogue could not be reached or refused (timeout, 429, 5xx).</summary>
    NetworkError,

    /// <summary>Online lookups are switched off in the settings.</summary>
    OnlineDisabled,

    /// <summary>A folder holding several books, which never gets a guessed cover.</summary>
    NotOneBook,

    /// <summary>The item lives in a movie, TV or other non-book library.</summary>
    NotBookLibrary,

    /// <summary>A cover would have been drawn but no font on the host can draw the text.</summary>
    NoFont
}

/// <summary>
/// The outcome of one cover request, as the scheduled task reports it.
/// </summary>
internal sealed record CoverOutcome(CoverReason Reason, string? Title, string? Author, string? Detail, string? Source);

/// <summary>
/// Where the provider leaves the outcome of each request, keyed by item id, for the
/// scheduled task to read. Jellyfin catches every provider exception and returns
/// nothing to the caller, so this is the only way the task learns why an item has
/// no cover. A DI singleton: the provider and the task get the same instance.
/// </summary>
public sealed class CoverOutcomeLog
{
    // A library scan records outcomes too; nothing reads those, so the map is capped.
    private const int MaxEntries = 20_000;

    private readonly ConcurrentDictionary<Guid, CoverOutcome> _outcomes = new();

    internal void Record(Guid itemId, CoverOutcome outcome)
    {
        if (_outcomes.Count >= MaxEntries)
        {
            _outcomes.Clear();
        }

        _outcomes[itemId] = outcome;
    }

    internal bool TryTake(Guid itemId, out CoverOutcome? outcome)
    {
        var found = _outcomes.TryRemove(itemId, out var value);
        outcome = value;
        return found;
    }

    internal void Clear() => _outcomes.Clear();
}
