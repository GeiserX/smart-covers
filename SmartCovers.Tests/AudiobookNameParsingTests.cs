using MediaBrowser.Controller.Entities;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// Audiobook folders are named to a handful of conventions. These tests pin down
/// the shapes that used to collapse into one unsearchable string.
/// </summary>
public class AudiobookNameParsingTests
{
    private static BaseItem Named(string name)
    {
        var mock = new Mock<BaseItem>();
        mock.SetupGet(m => m.Name).Returns(name);
        mock.SetupGet(m => m.Path).Returns((string)null!);
        return mock.Object;
    }

    [Theory]
    // "<Title> (format) <Author> <Year>" — the format tag marks the boundary.
    [InlineData("Quiet Harbour (mp3) Ana Ruiz 1984", "Quiet Harbour", "Ana Ruiz")]
    [InlineData("Quiet Harbour (mp3)  Ana Ruiz 2017", "Quiet Harbour", "Ana Ruiz")]
    [InlineData("Quiet Harbour (Mp3-Voz Humana)- Ana Ruiz", "Quiet Harbour", "Ana Ruiz")]
    [InlineData("Quiet Harbour (M4a-Voz Humana)- Ana Ruiz - Serie Puerto 5", "Quiet Harbour", "Ana Ruiz")]
    // A leading bracket tag that only says what the file is.
    [InlineData("[Audiolibro mp3] Quiet Harbour - Ana Ruiz 1989", "Quiet Harbour", "Ana Ruiz")]
    [InlineData("[Audiolibro mp3] - Quiet Harbour - Ana Ruiz 2008", "Quiet Harbour", "Ana Ruiz")]
    // A year hanging off the author.
    [InlineData("Quiet Harbour - Ana Ruiz 1989", "Quiet Harbour", "Ana Ruiz")]
    public void ParseBookInfo_AudiobookFolderShapes_SplitIntoTitleAndAuthor(
        string folderName, string expectedTitle, string expectedAuthor)
    {
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(Named("01"), folderName);

        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedAuthor, author);
    }

    [Fact]
    public void ParseBookInfo_ExtensionLeftInTheItemName_IsDropped()
    {
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(Named("Quiet Harbour (Unabridged).m4b"));

        Assert.Equal("Quiet Harbour (Unabridged)", title);
    }

    [Fact]
    public void ParseBookInfo_TitleEndingInAYear_KeepsIt()
    {
        // Only an author loses a trailing year; a title may legitimately be one.
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(Named("1984"));

        Assert.Equal("1984", title);
    }

    [Fact]
    public void ParseBookInfo_FolderNameWins_OverTheTrackName()
    {
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(
            Named("Pista  1"), "Quiet Harbour (mp3) Ana Ruiz 1984");

        Assert.Equal("Quiet Harbour", title);
        Assert.Equal("Ana Ruiz", author);
    }

    [Theory]
    [InlineData("Four Thousand Weeks: Time Management for Mortals", "Four Thousand Weeks")]
    [InlineData("Staff Engineer: Leadership Beyond the Management Track", "Staff Engineer")]
    [InlineData("Measure What Matters: OKRs: The Simple Idea", "Measure What Matters")]
    public void MainTitleOf_DropsTheSubtitle(string title, string expected)
        => Assert.Equal(expected, OnlineCoverFetcher.MainTitleOf(title));

    [Theory]
    [InlineData("Quiet Harbour")]                       // no subtitle to drop
    [InlineData("Build: An Unorthodox Guide")]          // one word matches everything
    [InlineData(": Leading With A Colon")]              // nothing before the colon
    public void MainTitleOf_RefusesWhenItWouldBeAGuess(string title)
        => Assert.Null(OnlineCoverFetcher.MainTitleOf(title));
}
