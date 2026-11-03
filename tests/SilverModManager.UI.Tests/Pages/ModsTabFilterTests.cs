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
/// The Mods tab lists what the app fetched for a Wabbajack modlist like any other library file, for the list's game
/// only, and does not list the modlist file itself (it is on the Collections tab).
/// </summary>
public class ModsTabFilterTests(IServiceProvider provider) : AUiTest(provider)
{
    private LibraryFilter Filter(Loadout.ReadOnly loadout)
        => new(loadout.LoadoutId, LocateStubbedInstallation<Cyberpunk2077Game>(Provider).Game);

    private NexusModsGameId NexusGame => LocateStubbedInstallation<Cyberpunk2077Game>(Provider).Game.NexusModsGameId.Value;

    /// <summary>What the library records for a file added for the loadout's game.</summary>
    private Destination ForTheGame => Destination.Of(LocateStubbedInstallation<Cyberpunk2077Game>(Provider).Game.GameId);

    /// <summary>
    /// A modlist for a game the app has no title for, which counts for whichever game is asked, unless a game type is given.
    /// Its own destination is what the recogniser leaves: the list's game, or unassigned for a game with no title.
    /// </summary>
    private async Task<EntityId> AddModlist(string name = "Some list", string gameType = "NoSuchGame")
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
        LibraryDestinations.Write(tx, id, SilverModManager.Wabbajack.WabbajackGames.GameIdFor(gameType) is { } listGame ? Destination.Of(listGame) : Destination.Unknown);
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
        LibraryDestinations.Write(tx, id, ForTheGame);
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private async Task<(EntityId Page, EntityId Item)> AddNexusFile(uint modId, uint fileId, string name, EntityId? existingPage = null)
    {
        using var tx = Connection.BeginTransaction();
        var game = NexusGame;
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
        LibraryDestinations.Write(tx, id, ForTheGame);
        var result = await tx.Commit();
        return (page is null ? existingPage!.Value : result.Remap(page).Id, result.Remap(item).Id);
    }

    [Fact]
    public async Task ANexusFileTheAppFetchedForAModlistIsInTheModsTab()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new NexusModsDataProvider(Provider);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();

        var (page, item) = await AddNexusFile(7001, 8001, "Fetched.zip");
        await AddArchiveRow(await AddModlist(), "Fetched.zip", fetched: item);

        await Eventually(() => rows.Lookup(page).HasValue.Should().BeTrue());
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
    public async Task AModPageWithOneFetchedAndOneOwnFileShowsBoth()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new NexusModsDataProvider(Provider);
        var (page, fetched) = await AddNexusFile(7003, 8003, "Fetched.zip");
        var (_, own) = await AddNexusFile(7003, 8004, "Own.zip", existingPage: page);
        await AddArchiveRow(await AddModlist(), "Fetched.zip", fetched: fetched);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();

        await Eventually(() => rows.Lookup(page).HasValue.Should().BeTrue());
        using var children = rows.Lookup(page).Value.ChildrenObservable.AsObservableCache();
        await Eventually(() => children.Keys.Should().BeEquivalentTo([fetched, own]));
    }

    [Fact]
    public async Task ALocalFileTheAppFetchedForAModlistIsInTheModsTabWithSourceWabbajack()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new LocalFileDataProvider(Provider);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();

        var own = await AddLocalFile("own.7z");
        var fetched = await AddLocalFile("fetched.7z");
        await AddArchiveRow(await AddModlist(), "fetched.7z", fetched: fetched);

        await Eventually(() => rows.Lookup(fetched).HasValue.Should().BeTrue());
        await Eventually(() => SourceOf(rows.Lookup(fetched).Value).Should().Be(ModSources.Wabbajack));
        await Eventually(() => rows.Lookup(own).HasValue.Should().BeTrue());
        SourceOf(rows.Lookup(own).Value).Should().Be(ModSources.LocalFile);
    }

    private static string SourceOf(CompositeItemModel<EntityId> row)
        => row.Get<StringComponent>(SharedColumns.Source.ComponentKey).Value.Value;

    [Fact]
    public async Task TheModlistFileItselfIsInNeitherProviderAndIsStillOfferedForGameRemoval()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        // A game the app has no title for shows in every library, so the row would appear here if the provider still emitted it.
        var list = await AddModlist("Listed", gameType: "NoSuchGame");
        var skyrimList = await AddModlist("Skyrim list", gameType: "SkyrimSpecialEdition");
        var local = new LocalFileDataProvider(Provider);
        var modlists = new WabbajackModlistDataProvider(Provider);
        using var localRows = local.ObserveLibraryItems(Filter(loadout)).AsObservableCache();
        using var listRows = modlists.ObserveLibraryItems(Filter(loadout)).AsObservableCache();

        await Task.Delay(300);

        localRows.Lookup(list).HasValue.Should().BeFalse();
        listRows.Count.Should().Be(0);
        var skyrim = SilverModManager.Wabbajack.WabbajackGames.GameIdFor("SkyrimSpecialEdition")!.Value;
        modlists.GetAllFiles(skyrim).Select(f => f.Id).Should().Contain(skyrimList);
    }

    [Fact]
    public async Task ALocalFileRowHasNoModPageOrChangelogToOpen()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Main");
        var provider = new LocalFileDataProvider(Provider);
        using var rows = provider.ObserveLibraryItems(Filter(loadout)).AsObservableCache();
        var local = await AddLocalFile("nopage.7z");

        await Eventually(() => rows.Lookup(local).HasValue.Should().BeTrue());

        var row = rows.Lookup(local).Value;
        row.Get<SharedComponents.ViewModPageAction>(LibraryColumns.Actions.ViewModPageComponentKey).IsEnabled.Value.Should().BeFalse();
        row.Get<LibraryComponents.ViewChangelogAction>(LibraryColumns.Actions.ViewChangelogComponentKey).IsEnabled.Value.Should().BeFalse();
    }
}
