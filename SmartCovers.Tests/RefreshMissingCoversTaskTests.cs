using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace SmartCovers.Tests;

/// <summary>
/// The task exists because Jellyfin only runs image providers when an item is new.
/// These tests pin down which items it picks up and, just as importantly, which it
/// leaves alone.
/// </summary>
public class RefreshMissingCoversTaskTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly Mock<ILibraryManager> _library = new();
    private readonly Mock<IProviderManager> _providers = new();

    public RefreshMissingCoversTaskTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"smartcovers-task-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>An image provider that only exists to carry a name.</summary>
    private sealed class NamedProvider(string name) : IImageProvider
    {
        public string Name { get; } = name;

        public bool Supports(BaseItem item) => true;
    }

    private RefreshMissingCoversTask CreateTask() =>
        new(NullLogger<RefreshMissingCoversTask>.Instance,
            _library.Object,
            _providers.Object,
            Mock.Of<IDirectoryService>());

    /// <summary>Makes Jellyfin report SmartCovers as an image fetcher for every item.</summary>
    private void SmartCoversIsEnabled() =>
        _providers
            .Setup(p => p.GetImageProviders(It.IsAny<BaseItem>(), It.IsAny<ImageRefreshOptions>()))
            .Returns<BaseItem, ImageRefreshOptions>((_, _) =>
                [new NamedProvider("Image Extractor"), new NamedProvider("SmartCovers")]);

    /// <summary>Makes Jellyfin report only other fetchers, as in a library we are off in.</summary>
    private void SmartCoversIsNotEnabled() =>
        _providers
            .Setup(p => p.GetImageProviders(It.IsAny<BaseItem>(), It.IsAny<ImageRefreshOptions>()))
            .Returns<BaseItem, ImageRefreshOptions>((_, _) =>
                [new NamedProvider("Image Extractor"), new NamedProvider("Epub Metadata")]);

    private static Book BookWithCover(string name = "Already Covered") =>
        new()
        {
            Name = name,
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = "/anywhere/cover.jpg" }]
        };

    private static Book BookWithoutCover(string name = "Quiet Harbour") =>
        new() { Name = name, ImageInfos = [] };

    private Folder BookFolder()
    {
        var dir = Path.Combine(_tmpDir, "Quiet Harbour (mp3) Ana Ruiz 1984");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "01 - Pista 1.mp3"), new byte[16]);
        return new Folder { Name = "Quiet Harbour (mp3) Ana Ruiz 1984", Path = dir, ImageInfos = [] };
    }

    private Folder ShelfFolder()
    {
        var dir = Path.Combine(_tmpDir, "Some Comic Collection");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Issue 01.cbr"), new byte[16]);
        File.WriteAllBytes(Path.Combine(dir, "Issue 02.cbr"), new byte[16]);
        return new Folder { Name = "Some Comic Collection", Path = dir, ImageInfos = [] };
    }

    [Fact]
    public void ItemThatAlreadyHasACover_IsSkipped()
    {
        SmartCoversIsEnabled();
        var task = CreateTask();

        Assert.False(task.IsCandidate(BookWithCover(), task.CreateRefreshOptions()));
    }

    [Fact]
    public void ItemWithNoCover_InALibraryWhereSmartCoversIsOff_IsSkipped()
    {
        SmartCoversIsNotEnabled();
        var task = CreateTask();

        Assert.False(task.IsCandidate(BookWithoutCover(), task.CreateRefreshOptions()));
    }

    [Fact]
    public void ItemWithNoCover_InAnEnabledLibrary_IsPickedUp()
    {
        SmartCoversIsEnabled();
        var task = CreateTask();

        Assert.True(task.IsCandidate(BookWithoutCover(), task.CreateRefreshOptions()));
    }

    [Fact]
    public void AudiobookFolderWithNoCover_IsPickedUp()
    {
        SmartCoversIsEnabled();
        var task = CreateTask();

        Assert.True(task.IsCandidate(BookFolder(), task.CreateRefreshOptions()));
    }

    [Fact]
    public void AFolderHoldingSeveralBooks_IsSkipped()
    {
        SmartCoversIsEnabled();
        var task = CreateTask();

        Assert.False(task.IsCandidate(ShelfFolder(), task.CreateRefreshOptions()));
    }

    [Fact]
    public async Task ExecuteAsync_RefreshesOnlyTheItemsWithoutACover()
    {
        SmartCoversIsEnabled();

        var withCover = BookWithCover();
        var withoutCover = BookWithoutCover();
        var shelf = ShelfFolder();
        var bookFolder = BookFolder();

        _library
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([withCover, withoutCover, shelf, bookFolder]);

        var refreshed = new List<BaseItem>();
        _providers
            .Setup(p => p.RefreshSingleItem(
                It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, MetadataRefreshOptions, CancellationToken>((item, _, _) => refreshed.Add(item))
            .ReturnsAsync(ItemUpdateType.ImageUpdate);

        var progress = new List<double>();
        await CreateTask().ExecuteAsync(new Progress<double>(progress.Add), CancellationToken.None);

        // Reference identity: BaseItem compares by name, which would hide a mix-up.
        Assert.Collection(
            refreshed,
            item => Assert.Same(withoutCover, item),
            item => Assert.Same(bookFolder, item));
    }

    [Fact]
    public async Task ExecuteAsync_QueriesForTheTypesSmartCoversHandles()
    {
        SmartCoversIsEnabled();

        InternalItemsQuery? seen = null;
        _library
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => seen = q)
            .Returns([]);

        await CreateTask().ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        Assert.NotNull(seen);
        Assert.True(seen!.Recursive);
        Assert.Equal(
            [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder],
            seen.IncludeItemTypes);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_StopsWithoutRefreshingTheRest()
    {
        SmartCoversIsEnabled();

        var items = Enumerable.Range(0, 5).Select(i => (BaseItem)BookWithoutCover($"Book {i}")).ToList();
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);

        using var cts = new CancellationTokenSource();
        var refreshed = 0;
        _providers
            .Setup(p => p.RefreshSingleItem(
                It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                refreshed++;
                cts.Cancel();
            })
            .ReturnsAsync(ItemUpdateType.ImageUpdate);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateTask().ExecuteAsync(new Progress<double>(_ => { }), cts.Token));

        Assert.Equal(1, refreshed);
    }

    [Fact]
    public async Task ExecuteAsync_OneItemThrows_TheRestStillRun()
    {
        SmartCoversIsEnabled();

        var items = Enumerable.Range(0, 3).Select(i => (BaseItem)BookWithoutCover($"Book {i}")).ToList();
        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(items);

        var attempts = 0;
        _providers
            .Setup(p => p.RefreshSingleItem(
                It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns<BaseItem, MetadataRefreshOptions, CancellationToken>((_, _, _) =>
            {
                attempts++;
                return attempts == 2
                    ? throw new IOException("unreadable")
                    : Task.FromResult(ItemUpdateType.ImageUpdate);
            });

        await CreateTask().ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        Assert.Equal(3, attempts);
    }

    [Fact]
    public void RefreshOptions_AskForImagesOnly_AndKeepExistingOnes()
    {
        var options = CreateTask().CreateRefreshOptions();

        Assert.Equal(MetadataRefreshMode.FullRefresh, options.ImageRefreshMode);
        Assert.Equal(MetadataRefreshMode.None, options.MetadataRefreshMode);
        Assert.False(options.ReplaceAllImages);
        Assert.False(options.ReplaceAllMetadata);
    }

    [Fact]
    public void TheTaskRunsDailyAtAQuietHour_AndIsVisibleInTheDashboard()
    {
        var task = CreateTask();
        var trigger = Assert.Single(task.GetDefaultTriggers());

        Assert.Equal(TaskTriggerInfoType.DailyTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(4).Ticks, trigger.TimeOfDayTicks);
        Assert.False(task.IsHidden);
        Assert.True(task.IsEnabled);
        Assert.NotEmpty(task.Key);
    }
}
