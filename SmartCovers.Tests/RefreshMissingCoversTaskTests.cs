using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using SmartCovers.Configuration;
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
    private readonly CoverOutcomeLog _outcomes = new();
    private readonly ListLogger<RefreshMissingCoversTask> _log = new();
    private bool _generatedCovers;

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
        new(_log,
            _library.Object,
            _providers.Object,
            Mock.Of<IDirectoryService>(),
            _outcomes)
        {
            ConfigurationSource = () => new PluginConfiguration { EnableGeneratedCovers = _generatedCovers }
        };

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
        // The order is by book, not by query order, so compare as a set.
        Assert.Equal(2, refreshed.Count);
        Assert.Contains(refreshed, item => ReferenceEquals(item, withoutCover));
        Assert.Contains(refreshed, item => ReferenceEquals(item, bookFolder));
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

    // ---- Drawn covers, library guard and reporting ----

    private void LibraryRootIs(string root, CollectionType type)
    {
        _library.Setup(l => l.GetContentType(It.IsAny<BaseItem>())).Returns(type);
        _library
            .Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns([new CollectionFolder { PhysicalLocationsList = [root] }]);
    }

    private string DrawnCoverFile()
    {
        var path = Path.Combine(_tmpDir, $"drawn-{Guid.NewGuid():N}.jpg");
        var bytes = new byte[2000];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xFE;
        bytes[4] = 0x00; bytes[5] = 0x1A;
        System.Text.Encoding.ASCII.GetBytes("SmartCovers:generated:v1").CopyTo(bytes, 6);
        File.WriteAllBytes(path, bytes);
        Assert.True(GeneratedCover.IsGeneratedFile(path));
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MusicTracksAreQueried_OnlyWhenDrawnCoversAreOn(bool generated)
    {
        _generatedCovers = generated;
        BaseItemKind[] expected = generated
            ? [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder, BaseItemKind.Audio]
            : [BaseItemKind.Book, BaseItemKind.AudioBook, BaseItemKind.Folder];

        Assert.Equal(expected, CreateTask().CandidateTypes());
    }

    [Fact]
    public async Task ItemWithADrawnCover_IsRefreshedSoARealOneCanReplaceIt()
    {
        SmartCoversIsEnabled();
        var drawn = new Book
        {
            Id = Guid.NewGuid(),
            Name = "Dizionario dei luoghi della Calabria",
            ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = DrawnCoverFile() }]
        };
        var real = BookWithCover();
        var task = CreateTask();

        Assert.True(task.IsCandidate(drawn, task.CreateRefreshOptions()));
        Assert.False(task.IsCandidate(real, task.CreateRefreshOptions()));

        _library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([drawn]);
        MetadataRefreshOptions? used = null;
        _providers
            .Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, MetadataRefreshOptions, CancellationToken>((_, o, _) => used = o)
            .ReturnsAsync(ItemUpdateType.ImageUpdate);

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        Assert.NotNull(used);
        Assert.Contains(ImageType.Primary, used!.ReplaceImages);
        Assert.False(used.ReplaceAllImages);
    }

    [Fact]
    public void MovieFolder_IsNotACandidate()
    {
        SmartCoversIsEnabled();
        LibraryRootIs(_tmpDir, CollectionType.movies);
        var dir = Path.Combine(_tmpDir, "Desmelenada (2018) [NF WEB-DL 2160p HEVC ES DD+ 5.1][HDO]");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Desmelenada.mkv"), new byte[16]);
        var task = CreateTask();

        Assert.False(task.IsCandidate(new Folder { Path = dir, ImageInfos = [] }, task.CreateRefreshOptions()));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void MusicTrack_IsACandidate_OnlyLooseAndWithDrawnCoversOn(bool generated, bool loose, bool expected)
    {
        SmartCoversIsEnabled();
        LibraryRootIs(_tmpDir, CollectionType.music);
        _generatedCovers = generated;
        var dir = loose ? _tmpDir : Directory.CreateDirectory(Path.Combine(_tmpDir, "Some Album")).FullName;
        var track = new Audio { Path = Path.Combine(dir, "Motiva2 - Gracias Sergio.mp3"), ImageInfos = [] };
        var task = CreateTask();

        Assert.Equal(expected, task.IsCandidate(track, task.CreateRefreshOptions()));
    }

    [Fact]
    public async Task Report_CountsKindsApart_OneLinePerBook_AndAReasonForEachMiss()
    {
        SmartCoversIsEnabled();
        LibraryRootIs(_tmpDir, CollectionType.books);

        var hacia = Path.Combine(_tmpDir, "Hacia la Fundación (mp3) - Isaac Asimov 1993");
        Directory.CreateDirectory(hacia);
        var tracks = Enumerable.Range(1, 3).Select(i =>
        {
            var path = Path.Combine(hacia, $"00{i}. Pista.mp3");
            File.WriteAllBytes(path, new byte[16]);
            return new AudioBook { Id = Guid.NewGuid(), Path = path, Name = $"00{i}. Pista", ImageInfos = [] };
        }).ToList();

        var build = Path.Combine(_tmpDir, "Build, An Unorthodox Guide to Making Things Worth Making");
        Directory.CreateDirectory(build);
        File.WriteAllBytes(Path.Combine(build, "1.mp3"), new byte[16]);
        var buildFolder = new Folder { Id = Guid.NewGuid(), Path = build, Name = Path.GetFileName(build), ImageInfos = [] };

        var rootFile = Path.Combine(_tmpDir, "Cuando se apagan las luces (Mp3-Voz Humana) -  Care Santos.mp3");
        File.WriteAllBytes(rootFile, new byte[16]);
        var single = new AudioBook { Id = Guid.NewGuid(), Path = rootFile, Name = "Cuando se apagan las luces", ImageInfos = [] };

        var belAbbes = new Book { Id = Guid.NewGuid(), Name = "Bel-Abbès et son arrondissement", ImageInfos = [] };
        var archivistica = new Book { Id = Guid.NewGuid(), Name = "Archivística en Navarra", ImageInfos = [] };

        _library
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns([.. tracks, buildFolder, single, belAbbes, archivistica]);

        var poster = Path.Combine(_tmpDir, "real.jpg");
        File.WriteAllBytes(poster, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);

        _providers
            .Setup(p => p.RefreshSingleItem(It.IsAny<BaseItem>(), It.IsAny<MetadataRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, MetadataRefreshOptions, CancellationToken>((item, _, _) =>
            {
                if (ReferenceEquals(item, buildFolder))
                {
                    // Found online: Jellyfin saved the image.
                    item.ImageInfos = [new ItemImageInfo { Type = ImageType.Primary, Path = poster }];
                    _outcomes.Record(item.Id, new CoverOutcome(CoverReason.FoundOnline, "Build", null, null, "Open Library"));
                }
                else if (ReferenceEquals(item, archivistica))
                {
                    _outcomes.Record(item.Id, new CoverOutcome(CoverReason.NetworkError, "Archivística en Navarra", null, "Open Library: 429 TooManyRequests", null));
                }
                else if (ReferenceEquals(item, belAbbes))
                {
                    // The provider never ran: another fetcher answered, or Jellyfin skipped it.
                }
                else if (item is AudioBook track && tracks.Contains(track))
                {
                    _outcomes.Record(item.Id, new CoverOutcome(CoverReason.NoOnlineMatch, "Hacia la Fundación", "Isaac Asimov", null, null));
                }
                else
                {
                    _outcomes.Record(item.Id, new CoverOutcome(CoverReason.NoOnlineMatch, "Cuando se apagan las luces", "Care Santos", null, null));
                }
            })
            .ReturnsAsync(ItemUpdateType.ImageUpdate);

        await CreateTask().ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var lines = _log.Messages;
        Assert.Contains(lines, l => l.Contains("2 books, 3 tracks in 1 audiobooks, 1 single-file audiobooks, 1 audiobook folders, 0 music tracks", StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("No cover for \"Hacia la Fundación\"", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("No cover for \"Hacia la Fundación\" by \"Isaac Asimov\" (audiobook, 3 tracks): no online match", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("network error (Open Library: 429 TooManyRequests)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Done: 1 got a real cover (0 books, 0 audiobook tracks, 1 audiobook folders", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Still without a cover, by reason: no online match 4, ", StringComparison.Ordinal)
            && l.Contains("network error 1", StringComparison.Ordinal)
            && l.Contains("provider not run 1", StringComparison.Ordinal));
    }
}

/// <summary>A logger that keeps every message, for asserting on log lines.</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return _messages.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_messages)
        {
            _messages.Add(formatter(state, exception));
        }
    }
}
