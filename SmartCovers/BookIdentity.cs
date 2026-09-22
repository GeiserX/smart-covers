using System.Text.RegularExpressions;

namespace SmartCovers;

/// <summary>
/// Works out which folder on disk carries a book's identity.
///
/// Jellyfin models every track of a folder-based audiobook as its own item whose
/// Name is just the track ("01", "Pista 1", "Chapter 3"). That name says nothing
/// about the book, so a cover lookup driven by it is worthless. The book's identity
/// lives in the folder name — and for multi-disc rips, in the folder ABOVE the
/// "CD 3" disc folder.
/// </summary>
internal static class BookIdentity
{
    // Disc folders: "CD1", "CD 1", "Disc 2", "Disco 3", "Disk 4", "DVD2" and the
    // "<book title> CD 4" shape. Deliberately narrow — "Part 2" / "Vol 3" are NOT
    // treated as discs, because those are how real series volumes get named and
    // collapsing them would look up the wrong book.
    private static readonly Regex DiscFolderRegex = new(
        @"^(?:.*\S[\s\-_\.]+)?(?:cd|disc|disco|disk|dvd)\s*[\-_\.]?\s*\d{1,3}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // A bare number is a disc folder too: "Book Title/1", "Book Title/2".
    // Capped at three digits so a folder named for a year ("1984") stays a title.
    private static readonly Regex BareNumberRegex = new(
        @"^\d{1,3}$",
        RegexOptions.Compiled);

    /// <summary>
    /// True when the folder name identifies a disc within a book rather than the book.
    /// </summary>
    internal static bool IsDiscFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        name = name.Trim();
        return BareNumberRegex.IsMatch(name) || DiscFolderRegex.IsMatch(name);
    }

    /// <summary>
    /// True when a directory holds one book rather than a shelf of them. A book
    /// folder either has no subdirectories at all, or only disc subdirectories;
    /// a library root or an author folder has subdirectories named after books.
    /// </summary>
    internal static bool IsBookFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateDirectories(dir)
                .All(sub => IsDiscFolderName(Path.GetFileName(sub)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the directory whose name is the book's identity.
    /// </summary>
    /// <param name="startDir">The directory the item lives in (or is).</param>
    /// <param name="libraryRootPath">
    /// The library root. The result is never this directory nor anything above it,
    /// so a loose file sitting in the library root never borrows the library's name.
    /// When null, only the structural <see cref="IsBookFolder"/> rule applies.
    /// </param>
    /// <returns>The identity directory, or null when the item has no book folder.</returns>
    internal static string? ResolveIdentityDirectory(string? startDir, string? libraryRootPath)
    {
        if (string.IsNullOrEmpty(startDir) || IsAtOrAboveRoot(startDir, libraryRootPath))
        {
            return null;
        }

        if (!IsBookFolder(startDir))
        {
            return null;
        }

        // A disc folder's identity is its parent — but only one level up, and never
        // past the library root.
        if (IsDiscFolderName(Path.GetFileName(startDir)))
        {
            var parent = Path.GetDirectoryName(startDir);
            if (!string.IsNullOrEmpty(parent)
                && !IsAtOrAboveRoot(parent, libraryRootPath)
                && IsBookFolder(parent))
            {
                return parent;
            }
        }

        return startDir;
    }

    /// <summary>
    /// True when <paramref name="dir"/> is the library root itself or one of its ancestors.
    /// </summary>
    private static bool IsAtOrAboveRoot(string dir, string? libraryRootPath)
    {
        if (string.IsNullOrEmpty(libraryRootPath))
        {
            return false;
        }

        var normalizedDir = Normalize(dir);
        var normalizedRoot = Normalize(libraryRootPath);

        if (normalizedDir.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Above the root means the root sits inside it.
        return normalizedRoot.StartsWith(
            normalizedDir + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
        => path.Replace('/', Path.DirectorySeparatorChar)
               .Replace('\\', Path.DirectorySeparatorChar)
               .TrimEnd(Path.DirectorySeparatorChar);
}
