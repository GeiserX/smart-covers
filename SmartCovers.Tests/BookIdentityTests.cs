using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// The book's identity lives in a folder name. These tests pin down which folder.
/// </summary>
public class BookIdentityTests : IDisposable
{
    private readonly string _tmpDir;

    public BookIdentityTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"smartcovers-identity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_tmpDir, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData("CD1")]
    [InlineData("CD 1")]
    [InlineData("cd-2")]
    [InlineData("Disc 2")]
    [InlineData("Disco 3")]
    [InlineData("Disk 4")]
    [InlineData("DVD2")]
    [InlineData("Quiet Harbour CD 4")]
    [InlineData("Quiet Harbour - CD 10")]
    [InlineData("1")]
    [InlineData("42")]
    public void IsDiscFolderName_DiscShapes_True(string name)
        => Assert.True(BookIdentity.IsDiscFolderName(name));

    [Theory]
    [InlineData("Quiet Harbour (mp3) Ana Ruiz 1984")]
    [InlineData("1984")]
    [InlineData("Quiet Harbour")]
    [InlineData("Part 2")]
    [InlineData("Volume 3")]
    [InlineData("")]
    [InlineData(null)]
    public void IsDiscFolderName_BookShapes_False(string? name)
        => Assert.False(BookIdentity.IsDiscFolderName(name));

    [Fact]
    public void IsBookFolder_OnlyDiscSubfolders_True()
    {
        var book = Dir("Quiet Harbour (mp3) Ana Ruiz 1984");
        Dir("Quiet Harbour (mp3) Ana Ruiz 1984", "CD1");
        Dir("Quiet Harbour (mp3) Ana Ruiz 1984", "CD2");

        Assert.True(BookIdentity.IsBookFolder(book));
    }

    [Fact]
    public void IsBookFolder_ShelfOfBooks_False()
    {
        Dir("Quiet Harbour (mp3) Ana Ruiz 1984");
        Dir("Distant Shore (mp3) Bo Lindqvist 1990");

        Assert.False(BookIdentity.IsBookFolder(_tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_TrackInBookFolder_ReturnsBookFolder()
    {
        var book = Dir("Quiet Harbour (mp3) Ana Ruiz 1984");
        Dir("Distant Shore (mp3) Bo Lindqvist 1990");

        Assert.Equal(book, BookIdentity.ResolveIdentityDirectory(book, _tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_DiscFolder_ClimbsToBookFolder()
    {
        var book = Dir("Quiet Harbour (mp3) Ana Ruiz 1984");
        var disc = Dir("Quiet Harbour (mp3) Ana Ruiz 1984", "CD3");
        Dir("Distant Shore (mp3) Bo Lindqvist 1990");

        Assert.Equal(book, BookIdentity.ResolveIdentityDirectory(disc, _tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_TitledDiscFolder_ClimbsToBookFolder()
    {
        var book = Dir("[Audiolibro mp3] Quiet Harbour - Ana Ruiz 1984");
        var disc = Dir("[Audiolibro mp3] Quiet Harbour - Ana Ruiz 1984", "Quiet Harbour CD 4");
        Dir("Distant Shore (mp3) Bo Lindqvist 1990");

        Assert.Equal(book, BookIdentity.ResolveIdentityDirectory(disc, _tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_LibraryRoot_ReturnsNull()
    {
        // A file loose in the library root has no book folder; borrowing the
        // library's own name would search a catalogue for the library.
        Dir("Quiet Harbour (mp3) Ana Ruiz 1984");

        Assert.Null(BookIdentity.ResolveIdentityDirectory(_tmpDir, _tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_FlatLibraryRoot_StillReturnsNull()
    {
        // No subfolders at all, so the structural rule alone would accept the root.
        // The explicit root boundary is what stops it.
        Assert.Null(BookIdentity.ResolveIdentityDirectory(_tmpDir, _tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_DiscFolderDirectlyUnderRoot_DoesNotClimbToRoot()
    {
        var disc = Dir("CD1");

        Assert.Equal(disc, BookIdentity.ResolveIdentityDirectory(disc, _tmpDir));
    }

    [Fact]
    public void ResolveIdentityDirectory_AuthorShelf_ReturnsNull()
    {
        // An author folder holding named book folders is not one book.
        var shelf = Dir("Ana Ruiz");
        Dir("Ana Ruiz", "Quiet Harbour");
        Dir("Ana Ruiz", "Distant Shore");

        Assert.Null(BookIdentity.ResolveIdentityDirectory(shelf, _tmpDir));
    }
}
