using Xunit;

namespace SmartCovers.Tests;

public class NaturalStringComparerTests
{
    private static int Compare(string? x, string? y) => NaturalStringComparer.Instance.Compare(x, y);

    [Theory]
    [InlineData("page-2", "page-10")]
    [InlineData("9", "10")]
    [InlineData("page2.jpg", "page10.jpg")]
    [InlineData("v1.2", "v1.10")]
    [InlineData("2abc", "10abc")]
    public void Compare_NumericRuns_CompareByValue(string smaller, string larger)
    {
        Assert.True(Compare(smaller, larger) < 0);
        Assert.True(Compare(larger, smaller) > 0);
    }

    [Theory]
    [InlineData("apple", "banana")]
    [InlineData("1a", "1b")]
    [InlineData("page", "pages")]
    public void Compare_NonNumeric_OrdinalOrder(string smaller, string larger)
    {
        Assert.True(Compare(smaller, larger) < 0);
        Assert.True(Compare(larger, smaller) > 0);
    }

    [Fact]
    public void Compare_CaseInsensitive()
    {
        Assert.Equal(0, Compare("PAGE-2", "page-2"));
    }

    [Fact]
    public void Compare_EqualStrings_ReturnsZero()
    {
        Assert.Equal(0, Compare("page-2.jpg", "page-2.jpg"));
    }

    [Fact]
    public void Compare_LeadingZeros_SameValue_FewerZerosFirst()
    {
        // Deterministic total order for equal numeric values.
        Assert.True(Compare("2", "002") < 0);
        Assert.True(Compare("002", "2") > 0);
        Assert.True(Compare("page-002", "page-2") > 0);
    }

    [Fact]
    public void Compare_LeadingZeros_DifferentValue_ByValue()
    {
        Assert.True(Compare("page-002", "page-10") < 0);
        Assert.True(Compare("010", "9") > 0);
    }

    [Fact]
    public void Compare_Nulls()
    {
        Assert.Equal(0, Compare(null, null));
        Assert.True(Compare(null, "a") < 0);
        Assert.True(Compare("a", null) > 0);
    }

    [Fact]
    public void Compare_PrefixShorterFirst()
    {
        Assert.True(Compare("page", "page-1") < 0);
    }

    [Fact]
    public void Compare_LongDigitRuns_NoOverflow()
    {
        // Far beyond long.MaxValue digits — must not overflow.
        var big = new string('9', 40);
        var bigger = "1" + new string('0', 40);
        Assert.True(Compare(big, bigger) < 0);
    }

    [Fact]
    public void Sort_ComicPages_NaturalOrder()
    {
        var pages = new[] { "page-10.jpg", "page-2.jpg", "page-1.jpg", "page-20.jpg", "page-3.jpg" };
        var sorted = pages.OrderBy(p => p, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(new[] { "page-1.jpg", "page-2.jpg", "page-3.jpg", "page-10.jpg", "page-20.jpg" }, sorted);
    }
}
