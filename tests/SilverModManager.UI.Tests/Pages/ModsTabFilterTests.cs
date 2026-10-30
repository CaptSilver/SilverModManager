using System.Collections.Immutable;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.Games.RedEngine.Cyberpunk2077;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Mods tab leaves out what the app fetched for a Wabbajack modlist, until the user installs it themselves, and
/// no longer lists the modlist file itself (it is on the Collections tab).
/// </summary>
public class ModsTabFilterTests(IServiceProvider provider) : AUiTest(provider)
{
    private LibraryFilter Filter(Loadout.ReadOnly loadout)
        => new(loadout.LoadoutId, LocateStubbedInstallation<Cyberpunk2077Game>(Provider).Game);

    private NexusModsGameId NexusGame => LocateStubbedInstallation<Cyberpunk2077Game>(Provider).Game.NexusModsGameId.Value;

    private async Task<EntityId> AddModlist(string name = "Some list", string gameType = "SkyrimSpecialEdition")
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
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    /// <summary>The list's row for an archive; <paramref name="fetched"/> is the library file the app fetched for it, if any.</summary>
    private async Task AddArchiveRow(EntityId modlist, string name, EntityId? fetched)
    {
        using var tx = Connection.BeginTransaction();
        var row = new WabbajackModlistArchive.New(tx)
        {
            ModlistId = modlist,
            Identity = FileIdentity.XxHash64(Hash.From((ulong)name.GetHashCode())),
            Size = Size.Zero,
            Name = name,
        };
        if (fetched is { } file) tx.Add(row.Id, WabbajackModlistArchive.FetchedFiles, file);
        await tx.Commit();
    }

    private async Task<EntityId> AddLocalFile(string name)
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
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    /// <summary>
    /// A Nexus game nothing else in the suite writes pages for. The host's database is shared, so a count of the
    /// stubbed game's pages moves whenever another test adds one; this game's count only moves for this test.
    /// </summary>
    private static readonly NexusModsGameId IsolatedNexusGame = NexusModsGameId.From(987_654);

    private LibraryFilter IsolatedFilter(Loadout.ReadOnly loadout) => new(loadout.LoadoutId, new IsolatedGame());

    /// <summary>Only what the Mods tab's filter reads: which Nexus game the pages belong to.</summary>
    private sealed class IsolatedGame : IGameData
    {
        public GameId GameId => GameId.From("isolated-nexus-game");
        public Optional<NexusModsGameId> NexusModsGameId => Optional.Some(IsolatedNexusGame);
        public StoreIdentifiers StoreIdentifiers => throw new NotSupportedException();
        public string DisplayName => "Isolated game";
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private async Task<(EntityId Page, EntityId Item)> AddNexusFile(uint modId, uint fileId, string name, EntityId? existingPage = null, NexusModsGameId? nexusGame = null)
    {
        using var tx = Connection.BeginTransaction();
        var game = nexusGame ?? NexusGame;
        var uid = new ModUid(ModId.From(modId), game);
        var page = existingPage is null
            ? new NexusModsModPageMetadata.New(tx)
            {
                Uid = uid,
                Name = "Some mod",
                GameDomain = GameDomain.From("cyberpunk2077"),
                UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
                DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            }
            : null;
        EntityId pageId = existingPage ?? page!.Id;
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(fileId), game),
            Name = name,
            Version = "1",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = pageId,
        };
        var bytes = System.Text.Encoding.UTF8.GetBytes($"{name}-{Guid.NewGuid()}");
        var item = new NexusModsLibraryItem.New(tx, out var id)
        {
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            FileMetadataId = metadata.Id,
            ModPageMetadataId = pageId,
        };
        _ = new LibraryFile.New(tx, id)
        {
            FileName = name,
            Hash = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = name },
        };
        var result = await tx.Commit();
        return (page is null ? existingPage!.Value : result.Remap(page).Id, result.Remap(item).Id);
    }

    /// <summary>What installing the library item into a loadout leaves: a group linked to it.</summary>
    private async Task Install(Loadout.ReadOnly loadout, EntityId libraryItem)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "installed" },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem, LoadoutItemGroup = group };
        await tx.Commit();
    }

    private static List<int> Counts(IObservable<int> counts)
    {
        var seen = new List<int>();
        counts.Subscribe(count => { lock (seen) seen.Add(count); });
        return seen;
    }

    private static int Last(List<int> seen) { lock (seen) return seen.Count == 0 ? -1 : seen[^1]; }

    [Fact]
    public async Task ANexusFileTheAppFetchedForAModlistIsNotInTheModsTabUntilTheUserInstallsIt()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var filter = IsolatedFilter(loadout);
        var provider = new NexusModsDataProvider(Provider);
        using var rows = provider.ObserveLibraryItems(filter).AsObservableCache();
        var counts = Counts(provider.CountLibraryItems(filter));
        await Eventually(() => Last(counts).Should().Be(0));

        var (page, item) = await AddNexusFile(7001, 8001, "Fetched.zip", nexusGame: IsolatedNexusGame);
        await AddArchiveRow(await AddModlist(), "Fetched.zip", fetched: item);
        // Changes reach the view in commit order, so once this page shows, the fetched file's page (written
        // first, and hidden by the archive row) has been dealt with; no fixed delay stands in for that.
        var (ownPage, _) = await AddNexusFile(7002, 8002, "Own.zip", nexusGame: IsolatedNexusGame);
        await Eventually(() => rows.Lookup(ownPage).HasValue.Should().BeTrue());
        await Eventually(() => Last(counts).Should().Be(1));

        rows.Lookup(page).HasValue.Should().BeFalse();

        await Install(loadout, item);

        await Eventually(() => rows.Lookup(page).HasValue.Should().BeTrue());
        await Eventually(() => Last(counts).Should().Be(2));
    }

    [Fact]
    public async Task ANexusFileTheUserDownloadedThemselvesStaysInTheModsTab()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new NexusModsDataProvider(Provider);
        var (page, item) = await AddNexusFile(7002, 8002, "Mine.zip");
        await AddArchiveRow(await AddModlist(), "Mine.zip", fetched: null);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();

        await Eventually(() => rows.Lookup(page).HasValue.Should().BeTrue());
    }

    [Fact]
    public async Task AModPageWithOneFetchedAndOneOwnFileShowsTheOwnFileOnly()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new NexusModsDataProvider(Provider);
        var (page, fetched) = await AddNexusFile(7003, 8003, "Fetched.zip");
        var (_, own) = await AddNexusFile(7003, 8004, "Own.zip", existingPage: page);
        await AddArchiveRow(await AddModlist(), "Fetched.zip", fetched: fetched);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();

        await Eventually(() => rows.Lookup(page).HasValue.Should().BeTrue());
        using var children = rows.Lookup(page).Value.ChildrenObservable.AsObservableCache();
        await Eventually(() => children.Keys.Should().BeEquivalentTo([own]));
    }

    [Fact]
    public async Task ALocalFileFromTheInboxIsHiddenOnlyWhileItIsFetchedAndNotInstalled()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new LocalFileDataProvider(Provider);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();
        var counts = Counts(provider.CountLibraryItems(Filter(loadout)));
        await Eventually(() => Last(counts).Should().BeGreaterThanOrEqualTo(0));
        await Task.Delay(200);
        var before = Last(counts);

        var fetched = await AddLocalFile("fetched.7z");
        var own = await AddLocalFile("own.7z");
        await AddArchiveRow(await AddModlist(), "fetched.7z", fetched: fetched);

        await Eventually(() => rows.Lookup(own).HasValue.Should().BeTrue());
        rows.Lookup(fetched).HasValue.Should().BeFalse();
        await Eventually(() => Last(counts).Should().Be(before + 1));

        await Install(loadout, fetched);

        await Eventually(() => rows.Lookup(fetched).HasValue.Should().BeTrue());
        await Eventually(() => Last(counts).Should().Be(before + 2));
    }

    [Fact]
    public async Task TheModlistFileItselfIsInNeitherProviderAndIsStillOfferedForGameRemoval()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        // A game the app has no title for shows in every library, so the row would appear here if the provider still emitted it.
        var list = await AddModlist("Listed", gameType: "NoSuchGame");
        var skyrimList = await AddModlist("Skyrim list");
        var local = new LocalFileDataProvider(Provider);
        var modlists = new WabbajackModlistDataProvider(Provider);
        using var localRows = local.ObserveLibraryItems(Filter(loadout)).AsObservableCache();
        using var listRows = modlists.ObserveLibraryItems(Filter(loadout)).AsObservableCache();
        var listCounts = Counts(modlists.CountLibraryItems(Filter(loadout)));

        await Task.Delay(300);

        localRows.Lookup(list).HasValue.Should().BeFalse();
        listRows.Count.Should().Be(0);
        Last(listCounts).Should().Be(0);
        var skyrim = SilverModManager.Wabbajack.WabbajackGames.GameIdFor("SkyrimSpecialEdition")!.Value;
        modlists.GetAllFiles(skyrim).Select(f => f.Id).Should().Contain(skyrimList);
    }
}
