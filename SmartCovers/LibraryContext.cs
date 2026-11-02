using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace SmartCovers;

/// <summary>
/// Which library an item lives in: its content type and its folders on disk.
/// </summary>
internal static class LibraryContext
{
    /// <summary>
    /// The content type of the item's library, or null when unknown or mixed.
    /// </summary>
    internal static CollectionType? ContentType(BaseItem item, ILibraryManager? libraryManager)
    {
        try
        {
            if (libraryManager != null)
            {
                return libraryManager.GetContentType(item);
            }

            return (item.GetTopParent() as CollectionFolder)?.CollectionType;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// True unless the item lives in a library of another kind (movies, TV, music,
    /// photos). Jellyfin enables image fetchers for plain folders in every library,
    /// so without this a movie's folder would be looked up as a book. A mixed
    /// library, or one that cannot be resolved, counts as a book library.
    /// </summary>
    internal static bool IsBookLibrary(BaseItem item, ILibraryManager? libraryManager)
        => ContentType(item, libraryManager) is null or CollectionType.books;

    /// <summary>
    /// The library folder on disk that holds the item (one of the library's paths),
    /// or null when it cannot be resolved.
    /// </summary>
    internal static string? RootPath(BaseItem item, ILibraryManager? libraryManager)
    {
        var roots = new List<string>();
        try
        {
            var folders = libraryManager?.GetCollectionFolders(item);
            if (folders is { Count: > 0 })
            {
                foreach (var folder in folders)
                {
                    roots.AddRange(folder is CollectionFolder collection ? collection.PhysicalLocations : [folder.Path]);
                }
            }
            else
            {
                var top = item.GetTopParent();
                if (top is CollectionFolder collection)
                {
                    roots.AddRange(collection.PhysicalLocations);
                }
                else if (top != null)
                {
                    roots.Add(top.Path);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }

        roots.RemoveAll(string.IsNullOrEmpty);
        if (roots.Count == 0)
        {
            return null;
        }

        var path = item.Path;
        if (!string.IsNullOrEmpty(path))
        {
            var holding = roots.FirstOrDefault(r => IsSameOrInside(path, r));
            if (holding != null)
            {
                return holding;
            }
        }

        return roots[0];
    }

    /// <summary>
    /// True when <paramref name="dir"/> is <paramref name="expected"/>.
    /// </summary>
    internal static bool IsSameDirectory(string? dir, string? expected)
        => !string.IsNullOrEmpty(dir)
            && !string.IsNullOrEmpty(expected)
            && string.Equals(Trim(dir), Trim(expected), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string path, string root)
    {
        var p = Trim(path);
        var r = Trim(root);
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) => path.TrimEnd('/', '\\');
}
