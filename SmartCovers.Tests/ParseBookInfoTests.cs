using Moq;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace SmartCovers.Tests;

public class ParseBookInfoTests
{
    private static BaseItem CreateBookWithName(string name)
    {
        var mock = new Mock<BaseItem>();
        mock.SetupGet(m => m.Name).Returns(name);
        mock.SetupGet(m => m.Path).Returns((string?)null);
        return mock.Object;
    }

    [Fact]
    public void ParseBookInfo_SimpleTitle_ReturnsTitleNoAuthor()
    {
        var item = CreateBookWithName("The Great Gatsby");
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.Equal("The Great Gatsby", title);
        Assert.Null(author);
    }

    [Fact]
    public void ParseBookInfo_TitleDashAuthor_SplitsCorrectly()
    {
        var item = CreateBookWithName("Don Quijote - Miguel de Cervantes");
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.Equal("Don Quijote", title);
        Assert.Equal("Miguel de Cervantes", author);
    }

    [Fact]
    public void ParseBookInfo_FormatTag_Removed()
    {
        var item = CreateBookWithName("My Book (Mp3 128kbps)");
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.DoesNotContain("Mp3", title);
        Assert.Equal("My Book", title);
    }

    [Fact]
    public void ParseBookInfo_LocaleTag_Removed()
    {
        var item = CreateBookWithName("El Principito [Castellano]");
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.DoesNotContain("Castellano", title);
        Assert.Equal("El Principito", title);
    }

    [Fact]
    public void ParseBookInfo_YearAndAuthorInParens_ExtractsAuthor()
    {
        var item = CreateBookWithName("A solas (2019, Silvia Congost)");
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.Equal("A solas", title);
        Assert.Equal("Silvia Congost", author);
    }

    [Fact]
    public void ParseBookInfo_TrailingYear_Removed()
    {
        var item = CreateBookWithName("Some Book - 2020");
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.DoesNotContain("2020", title);
    }

    [Fact]
    public void ParseBookInfo_AudibleCode_Removed()
    {
        var item = CreateBookWithName("My Audiobook [B012ABC345]");
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.DoesNotContain("B012ABC345", title);
        Assert.Equal("My Audiobook", title);
    }

    [Fact]
    public void ParseBookInfo_SagaSuffix_Removed()
    {
        var item = CreateBookWithName("Dune - Saga completa");
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.Equal("Dune", title);
    }

    [Fact]
    public void ParseBookInfo_ParenYear_Removed()
    {
        var item = CreateBookWithName("Title (2021)");
        var (title, _) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.DoesNotContain("2021", title);
        Assert.Equal("Title", title);
    }

    [Fact]
    public void ParseBookInfo_EmptyName_ReturnsEmpty()
    {
        var item = CreateBookWithName("");
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.Equal(string.Empty, title);
        Assert.Null(author);
    }

    [Fact]
    public void ParseBookInfo_MultipleDashes_UsesLastDash()
    {
        var item = CreateBookWithName("Harry Potter - and the Philosopher's Stone - J.K. Rowling");
        var (title, author) = OnlineCoverFetcher.ParseBookInfo(item);
        Assert.Equal("Harry Potter - and the Philosopher's Stone", title);
        Assert.Equal("J.K. Rowling", author);
    }
}
