using System.Collections.Immutable;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The library and loadout providers read the slice of items destined for the game they are asked about, not every
/// item the library holds. Two games share one Nexus id here, the way Skyrim SE and Skyrim VR do, so a provider that
/// still matches on the Nexus id lists the sibling's items too.
/// </summary>
public class LibraryDestinationProviderTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId NoLoadout = LoadoutId.From(EntityId.From(0xBEEF));

    private static int _nextNexusId = 960_000;

    /// <summary>Two games, a fresh pair per test, so a slice holds nothing another test wrote.</summary>
    private sealed class TestGame(GameId gameId, NexusModsGameId nexusId) : IGameData
    {
        public GameId GameId { get; } = gameId;
        public Optional<NexusModsGameId> NexusModsGameId => Optional.Some(nexusId);
        public StoreIdentifiers StoreIdentifiers => new(GameId);
        public string DisplayName => "Test game " + GameId;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private static (TestGame Game, TestGame Sibling) NewPair()
    {
        var nexus = NexusModsGameId.From((uint)Interlocked.Increment(ref _nextNexusId));
        return (new TestGame(GameId.From("game-" + Guid.NewGuid().ToString("N")), nexus),
            new TestGame(GameId.From("sibling-" + Guid.NewGuid().ToString("N")), nexus));
    }

    private static LibraryFilter Filter(IGameData game) => new(NoLoadout, game);

    private static Destination Both(IGameData a, IGameData b) => new(ImmutableHashSet.Create(a.GameId, b.GameId), Shared: false);

    private async Task<EntityId> AddLocalFile(string name, Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}" };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private async Task<EntityId> AddArchive(string name, Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        _ = new ManuallyCreatedArchive.New(tx, id)
        {
            LibraryArchive = new LibraryArchive.New(tx, id) { LibraryFile = file, IsArchive = true },
            Source = ManuallyCreatedArchive.CreationSource.CollectionBundled,
        };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private async Task<EntityId> AddModlist(string name, string gameType, Destination destination)
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = $"{name}.wabbajack",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = $"{name}.wabbajack" },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = $"/x/{name}.wabbajack" };
        _ = new WabbajackModlistLibraryFile.New(tx, id)
        {
            LibraryFile = file, Name = name, Author = "Someone", Version = "1.0", GameType = gameType,
            ArchiveCount = 0, ArchiveSize = Size.Zero, InstallSize = Size.Zero, IsNsfw = false,
        };
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    /// <summary>A Nexus file on a page of the shared Nexus id; <paramref name="withFile"/> false leaves out the library file.</summary>
    private async Task<(EntityId Page, EntityId Item)> AddNexusFile(
        NexusModsGameId nexus, uint modId, uint fileId, string name, Destination destination,
        EntityId? existingPage = null, bool withFile = true)
    {
        using var tx = Connection.BeginTransaction();
        var page = existingPage is null
            ? new NexusModsModPageMetadata.New(tx)
            {
                Uid = new ModUid(ModId.From(modId), nexus),
                Name = "Some mod " + name,
                GameDomain = GameDomain.From("cyberpunk2077"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            }
            : null;
        EntityId pageId = existingPage ?? page!.Id;
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(fileId), nexus),
            Name = name,
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = pageId,
        };
        var item = new NexusModsLibraryItem.New(tx, out var id)
        {
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            FileMetadataId = metadata.Id,
            ModPageMetadataId = pageId,
        };
        if (withFile)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"{name}-{Guid.NewGuid()}");
            _ = new LibraryFile.New(tx, id)
            {
                FileName = name,
                Hash = bytes.xxHash3(),
                Size = Size.FromLong(bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            };
        }
        LibraryDestinations.Write(tx, id, destination);
        var result = await tx.Commit();
        return (page is null ? existingPage!.Value : result.Remap(page).Id, result.Remap(item).Id);
    }

    private async Task<LoadoutId> AddLoadout(IGameData game)
    {
        using var tx = Connection.BeginTransaction();
        var metadata = new GameInstallMetadata.New(tx)
        {
            Path = "/nonexistent/" + Guid.NewGuid().ToString("N"),
            Name = game.DisplayName,
            Store = GameStore.Unknown,
            Game = game.GameId,
        };
        var loadout = new Loadout.New(tx)
        {
            Name = "Destination loadout",
            ShortName = "DST",
            InstallationId = metadata,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(loadout).LoadoutId;
    }

    /// <summary>What installing a library item into a loadout leaves: a linked group, and the loadout's game on the item.</summary>
    private async Task Install(LoadoutId loadout, EntityId libraryItem)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = "installed" },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem, LoadoutItemGroup = group };
        LibraryDestinations.Add(tx, libraryItem, new LoadoutAcquisition(loadout).Destination(Connection.Db));
        await tx.Commit();
    }

    private static LoadoutFilter WholeLoadout(LoadoutId loadout) => new()
    {
        LoadoutId = loadout,
        CollectionGroupId = Optional<LoadoutItemGroupId>.None,
    };

    private static IObservableCache<CompositeItemModel<EntityId>, EntityId> Rows(IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> changes)
        => changes.AsObservableCache();

    /// <summary>Records the ids of every item any changeset adds, so a row that appears and goes again is still seen.</summary>
    private sealed class KeyRecorder : IDisposable
    {
        private readonly List<EntityId> _keys = [];
        private readonly IDisposable _subscription;

        public KeyRecorder(IObservable<IChangeSet<CompositeItemModel<EntityId>, EntityId>> changes)
        {
            _subscription = changes.Subscribe(changeSet =>
            {
                lock (_keys) _keys.AddRange(changeSet.Select(static change => change.Key));
            });
        }

        public EntityId[] Keys { get { lock (_keys) return _keys.ToArray(); } }
        public void Dispose() => _subscription.Dispose();
    }

    [Fact]
    public async Task LocalFile_ForTheSibling_IsNotListedUnderTheGame()
    {
        var (game, sibling) = NewPair();
        var theirs = await AddLocalFile("theirs.7z", Destination.Of(sibling.GameId));
        var mine = await AddLocalFile("mine.7z", Destination.Of(game.GameId));
        var provider = new LocalFileDataProvider(Provider);

        using var rows = Rows(provider.ObserveLibraryItems(Filter(game)));
        using var siblingRows = Rows(provider.ObserveLibraryItems(Filter(sibling)));

        // Changes reach the view in commit order, so once mine shows, theirs has had its chance.
        await Eventually(() => rows.Lookup(mine).HasValue.Should().BeTrue());
        await Eventually(() => siblingRows.Lookup(theirs).HasValue.Should().BeTrue());
        rows.Lookup(theirs).HasValue.Should().BeFalse();
        siblingRows.Lookup(mine).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task NexusItem_ForTheSiblingOnly_IsNotListedUnderTheGame_EvenThoughTheNexusIdMatches()
    {
        var (game, sibling) = NewPair();
        var nexus = game.NexusModsGameId.Value;
        var (theirsPage, _) = await AddNexusFile(nexus, 1, 1, "theirs.zip", Destination.Of(sibling.GameId));
        var (minePage, _) = await AddNexusFile(nexus, 2, 2, "mine.zip", Destination.Of(game.GameId));
        var provider = new NexusModsDataProvider(Provider);

        using var rows = Rows(provider.ObserveLibraryItems(Filter(game)));
        using var siblingRows = Rows(provider.ObserveLibraryItems(Filter(sibling)));

        await Eventually(() => rows.Lookup(minePage).HasValue.Should().BeTrue());
        await Eventually(() => siblingRows.Lookup(theirsPage).HasValue.Should().BeTrue());
        rows.Lookup(theirsPage).HasValue.Should().BeFalse();
        siblingRows.Lookup(minePage).HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task NexusItem_ForBoth_IsListedUnderBoth()
    {
        var (game, sibling) = NewPair();
        var (page, _) = await AddNexusFile(game.NexusModsGameId.Value, 3, 3, "both.zip", Both(game, sibling));
        var provider = new NexusModsDataProvider(Provider);

        using var rows = Rows(provider.ObserveLibraryItems(Filter(game)));
        using var siblingRows = Rows(provider.ObserveLibraryItems(Filter(sibling)));

        await Eventually(() => rows.Lookup(page).HasValue.Should().BeTrue());
        await Eventually(() => siblingRows.Lookup(page).HasValue.Should().BeTrue());
    }

    /// <remarks>The "No game assigned" tag on the row is the row-actions work; this pins that the row is listed.</remarks>
    [Fact]
    public async Task Unassigned_IsListedUnderEveryGame()
    {
        var (game, sibling) = NewPair();
        var local = await AddLocalFile("unassigned.7z", Destination.Unknown);
        var (page, _) = await AddNexusFile(game.NexusModsGameId.Value, 4, 4, "unassigned.zip", Destination.Unknown);
        var localProvider = new LocalFileDataProvider(Provider);
        var nexusProvider = new NexusModsDataProvider(Provider);

        foreach (var forGame in new[] { game, sibling })
        {
            using var localRows = Rows(localProvider.ObserveLibraryItems(Filter(forGame)));
            using var nexusRows = Rows(nexusProvider.ObserveLibraryItems(Filter(forGame)));

            await Eventually(() => localRows.Lookup(local).HasValue.Should().BeTrue());
            await Eventually(() => nexusRows.Lookup(page).HasValue.Should().BeTrue());
        }
    }

    [Fact]
    public async Task ObserveLibraryItems_NeverEmitsAnotherGamesItems()
    {
        var (game, sibling) = NewPair();
        var nexus = game.NexusModsGameId.Value;
        var localProvider = new LocalFileDataProvider(Provider);
        var nexusProvider = new NexusModsDataProvider(Provider);
        var siblingsFiles = new List<EntityId>();
        var siblingsPages = new List<EntityId>();

        // Half the sibling's items exist before the subscription, so the first emission is covered too.
        for (var i = 0; i < 25; i++)
        {
            siblingsFiles.Add(await AddLocalFile($"before-{i}.7z", Destination.Of(sibling.GameId)));
            siblingsPages.Add((await AddNexusFile(nexus, (uint)(100 + i), (uint)(100 + i), $"before-{i}.zip", Destination.Of(sibling.GameId))).Page);
        }

        using var localKeys = new KeyRecorder(localProvider.ObserveLibraryItems(Filter(game)));
        using var nexusKeys = new KeyRecorder(nexusProvider.ObserveLibraryItems(Filter(game)));
        using var siblingLocal = Rows(localProvider.ObserveLibraryItems(Filter(sibling)));
        using var siblingNexus = Rows(nexusProvider.ObserveLibraryItems(Filter(sibling)));

        for (var i = 0; i < 25; i++)
        {
            siblingsFiles.Add(await AddLocalFile($"after-{i}.7z", Destination.Of(sibling.GameId)));
            siblingsPages.Add((await AddNexusFile(nexus, (uint)(200 + i), (uint)(200 + i), $"after-{i}.zip", Destination.Of(sibling.GameId))).Page);
        }

        // The sibling's own view has them all, so every commit has reached the providers.
        await Eventually(() => siblingLocal.Keys.Should().Contain(siblingsFiles));
        await Eventually(() => siblingNexus.Keys.Should().Contain(siblingsPages));
        // Unassigned items from other tests show under every game, so "none of the sibling's" is the claim.
        localKeys.Keys.Should().NotContain(siblingsFiles);
        nexusKeys.Keys.Should().NotContain(siblingsPages);
    }

    /// <remarks>
    /// The emitted keys are what a test can see. Whether a provider also loaded the sibling's items without
    /// emitting them has no observable, so this does not claim that.
    /// </remarks>
    [Fact]
    public async Task ObserveLoadoutItems_NeverSubscribesToTheSiblingsItems()
    {
        var (game, sibling) = NewPair();
        var nexus = game.NexusModsGameId.Value;
        var gameLoadout = await AddLoadout(game);
        var siblingLoadout = await AddLoadout(sibling);
        var localProvider = new LocalFileDataProvider(Provider);
        var nexusProvider = new NexusModsDataProvider(Provider);
        var siblingsFiles = new List<EntityId>();
        var siblingsPages = new List<EntityId>();

        for (var i = 0; i < 50; i++)
        {
            var file = await AddLocalFile($"installed-{i}.7z", Destination.Of(sibling.GameId));
            await Install(siblingLoadout, file);
            siblingsFiles.Add(file);
            var (page, item) = await AddNexusFile(nexus, (uint)(300 + i), (uint)(300 + i), $"installed-{i}.zip", Destination.Of(sibling.GameId));
            await Install(siblingLoadout, item);
            siblingsPages.Add(page);
        }

        using var localKeys = new KeyRecorder(localProvider.ObserveLoadoutItems(WholeLoadout(gameLoadout)));
        using var nexusKeys = new KeyRecorder(nexusProvider.ObserveLoadoutItems(WholeLoadout(gameLoadout)));
        using var siblingLocal = Rows(localProvider.ObserveLoadoutItems(WholeLoadout(siblingLoadout)));
        using var siblingNexus = Rows(nexusProvider.ObserveLoadoutItems(WholeLoadout(siblingLoadout)));

        await Eventually(() => siblingLocal.Keys.Should().Contain(siblingsFiles));
        await Eventually(() => siblingNexus.Keys.Should().Contain(siblingsPages));
        localKeys.Keys.Should().BeEmpty("only items linked into the loadout are listed, and the sibling's are in its own loadout");
        nexusKeys.Keys.Should().BeEmpty();
    }

    [Fact]
    public async Task ObserveLoadoutItems_ListsTheGamesInstalledItems()
    {
        var (game, sibling) = NewPair();
        var gameLoadout = await AddLoadout(game);
        var installed = await AddLocalFile("installed.7z", Destination.Of(game.GameId));
        var notInstalled = await AddLocalFile("downloaded.7z", Destination.Of(game.GameId));
        var (page, item) = await AddNexusFile(game.NexusModsGameId.Value, 5, 5, "installed.zip", Destination.Of(game.GameId));
        await Install(gameLoadout, installed);
        await Install(gameLoadout, item);

        using var local = Rows(new LocalFileDataProvider(Provider).ObserveLoadoutItems(WholeLoadout(gameLoadout)));
        using var nexus = Rows(new NexusModsDataProvider(Provider).ObserveLoadoutItems(WholeLoadout(gameLoadout)));

        await Eventually(() => local.Keys.Should().Contain(installed));
        await Eventually(() => nexus.Keys.Should().Contain(page));
        local.Keys.Should().NotContain(notInstalled);
    }

    [Fact]
    public async Task IsShownInMods_HeldFileAdoptedByTheSiblingsList_StaysVisibleUnderTheGame()
    {
        // The two games a modlist can name that share a Nexus id; the list is the sibling's, the file is for both.
        var game = new TestGame(WabbajackGames.GameIdFor("SkyrimSpecialEdition")!.Value, NexusModsGameId.From(1704));
        var sibling = new TestGame(WabbajackGames.GameIdFor("SkyrimVR")!.Value, NexusModsGameId.From(1704));
        var held = await AddLocalFile("held.7z", Both(game, sibling));
        var list = await AddModlist("Yggdrasil VR " + Guid.NewGuid().ToString("N"), "SkyrimVR", Destination.Of(sibling.GameId));
        using (var tx = Connection.BeginTransaction())
        {
            var row = new WabbajackModlistArchive.New(tx)
            {
                ModlistId = list,
                Identity = FileIdentity.XxHash64(Hash.From((ulong)Guid.NewGuid().GetHashCode())),
                Size = Size.Zero,
                Name = "held.7z",
            };
            tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, held);
            await tx.Commit();
        }
        var provider = new LocalFileDataProvider(Provider);

        using var rows = Rows(provider.ObserveLibraryItems(Filter(game)));
        using var siblingRows = Rows(provider.ObserveLibraryItems(Filter(sibling)));
        // Added after the fetch mark. A row can show for an instant before its fetch mark is read, so the held file
        // is only judged once both views have shown a later item and had a moment to settle.
        var marker = await AddLocalFile("marker.7z", Destination.Of(sibling.GameId));
        var gameMarker = await AddLocalFile("game marker.7z", Destination.Of(game.GameId));

        await Eventually(() => siblingRows.Lookup(marker).HasValue.Should().BeTrue());
        await Eventually(() => rows.Lookup(gameMarker).HasValue.Should().BeTrue());
        await Task.Delay(300);
        rows.Lookup(held).HasValue.Should().BeTrue("no list for this game fetched it");
        siblingRows.Lookup(held).HasValue.Should().BeFalse("the sibling's own list fetched it");
    }

    [Fact]
    public async Task GetAllFiles_LocalFiles_ReturnsTheGamesFiles()
    {
        var (game, sibling) = NewPair();
        var mine = await AddLocalFile("mine.7z", Destination.Of(game.GameId));
        var theirs = await AddLocalFile("theirs.7z", Destination.Of(sibling.GameId));
        var both = await AddLocalFile("both.7z", Both(game, sibling));
        await AddLocalFile("unassigned.7z", Destination.Unknown);
        var archive = await AddArchive("bundled", Destination.Of(game.GameId));
        var siblingsArchive = await AddArchive("their bundle", Destination.Of(sibling.GameId));

        new LocalFileDataProvider(Provider).GetAllFiles(game.GameId).Select(file => file.Id)
            .Should().BeEquivalentTo([mine, both]);
        new LocalFileDataProvider(Provider).GetAllFiles(sibling.GameId).Select(file => file.Id)
            .Should().BeEquivalentTo([theirs, both]);
        new ManuallyCreatedArchiveDataProvider(Provider).GetAllFiles(game.GameId).Select(file => file.Id)
            .Should().BeEquivalentTo([archive]);
        new ManuallyCreatedArchiveDataProvider(Provider).GetAllFiles(sibling.GameId).Select(file => file.Id)
            .Should().BeEquivalentTo([siblingsArchive]);
    }

    [Fact]
    public async Task GetAllFiles_AModlist_IsReturnedByTheModlistProviderOnly()
    {
        var (game, _) = NewPair();
        var list = await AddModlist("A list " + Guid.NewGuid().ToString("N"), "NoSuchGame", Destination.Of(game.GameId));

        new WabbajackModlistDataProvider(Provider).GetAllFiles(game.GameId).Select(file => file.Id).Should().Equal(list);
        new LocalFileDataProvider(Provider).GetAllFiles(game.GameId).Should().BeEmpty("removing a game must not delete the list twice");
    }

    [Fact]
    public async Task GetAllFiles_NexusItemWithoutAFile_IsSkipped()
    {
        var (game, _) = NewPair();
        var nexus = game.NexusModsGameId.Value;
        await AddNexusFile(nexus, 6, 6, "no file.zip", Destination.Of(game.GameId), withFile: false);
        var (_, withFile) = await AddNexusFile(nexus, 7, 7, "with file.zip", Destination.Of(game.GameId));

        new NexusModsDataProvider(Provider).GetAllFiles(game.GameId).Select(file => file.Id).Should().Equal(withFile);
    }

    [Fact]
    public async Task CountLibraryItems_CountsTheRowsOfTheGameOnly()
    {
        var (game, sibling) = NewPair();
        var theirs = await AddLocalFile("theirs.7z", Destination.Of(sibling.GameId));
        var mine = await AddLocalFile("mine.7z", Destination.Of(game.GameId));
        var provider = new LocalFileDataProvider(Provider);
        var counts = new List<int>();
        using var _ = provider.CountLibraryItems(Filter(game)).Subscribe(count => { lock (counts) counts.Add(count); });
        using var rows = Rows(provider.ObserveLibraryItems(Filter(game)));

        // Other tests' unassigned files are listed under every game, so the count is checked against the rows.
        await Eventually(() => rows.Lookup(mine).HasValue.Should().BeTrue());
        await Eventually(() => { lock (counts) counts.Should().EndWith(rows.Count); });
        rows.Lookup(theirs).HasValue.Should().BeFalse();
    }
}
