using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Pages;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// "View mod page" and "View changelog" resolve the item's source instead of assuming Nexus. The os is a substitute
/// so no test opens a browser.
/// </summary>
public class ModPageActionTests(IServiceProvider provider) : AUiTest(provider)
{
    private static uint _nextModId = 91000;

    private sealed class AdapterProbe(IServiceProvider provider, LibraryFilter filter) : LibraryTreeDataGridAdapter(provider, filter)
    {
        public void Hook(CompositeItemModel<EntityId> model) => BeforeModelActivationHook(model);
    }

    private ModSourceResolver Resolver => Provider.GetRequiredService<ModSourceResolver>();

    private async Task<(EntityId Item, uint ModId)> AddNexusItem()
    {
        var modId = _nextModId++;
        var game = NexusModsGameId.From(4333);
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes("nexus " + modId);
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = "mod.zip",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "A mod" },
        };
        var page = new NexusModsModPageMetadata.New(tx)
        {
            Uid = new ModUid(ModId.From(modId), game),
            Name = "A mod",
            GameDomain = GameDomain.From("eldenring"),
            UpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            DataUpdatedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
        };
        var metadata = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(modId), game),
            Name = "mod.zip",
            Version = "1.0",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        tx.Add(id, NexusModsLibraryItem.FileMetadataId, metadata.Id);
        tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        var result = await tx.Commit();
        return (result.Remap(file).Id, modId);
    }

    private async Task<EntityId> AddLocalItem()
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes("local " + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = "local.zip",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "local.zip" },
        };
        _ = new LocalFile.New(tx, id) { LibraryFile = file, OriginalPath = "/x/local.zip" };
        var result = await tx.Commit();
        return result.Remap(file).Id;
    }

    private async Task<LoadoutItemId> Link(LoadoutId loadout, EntityId libraryItem)
    {
        using var tx = Connection.BeginTransaction();
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout, Name = "installed" },
        };
        _ = new LibraryLinkedLoadoutItem.New(tx, id) { LibraryItemId = libraryItem, LoadoutItemGroup = group };
        var result = await tx.Commit();
        return LoadoutItemId.From(result[id]);
    }

    [Fact]
    public async Task ANexusRowsViewModPageClick_CarriesTheRowsLibraryItemIds()
    {
        var (item, _) = await AddNexusItem();
        var (game, _) = TestGame.NewPair();
        var loadout = await TestGame.AddLoadout(Connection, game);
        var row = new CompositeItemModel<EntityId>(item);
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(LibraryItemId.From(item)));
        var action = new SharedComponents.ViewModPageAction();
        row.Add(LibraryColumns.Actions.ViewModPageComponentKey, action);
        var adapter = new AdapterProbe(Provider, new LibraryFilter(loadout, game));
        var messages = new List<SilverModManager.App.UI.Pages.LibraryPage.ViewModPageMessage>();
        using var subscription = adapter.MessageSubject.Subscribe(message => message.Switch(_ => { }, _ => { }, _ => { }, _ => { }, messages.Add, _ => { }, _ => { }, _ => { }));

        await OnUi(() =>
        {
            adapter.Hook(row);
            action.CommandViewModPage.Execute(Unit.Default);
            return Task.CompletedTask;
        });

        messages.Should().ContainSingle().Which.Ids.Should().Equal(LibraryItemId.From(item));
    }

    [Fact]
    public async Task ANexusRowsViewChangelogClick_CarriesTheRowsLibraryItemIds()
    {
        var (item, _) = await AddNexusItem();
        var (game, _) = TestGame.NewPair();
        var loadout = await TestGame.AddLoadout(Connection, game);
        var row = new CompositeItemModel<EntityId>(item);
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(LibraryItemId.From(item)));
        var action = new LibraryComponents.ViewChangelogAction();
        row.Add(LibraryColumns.Actions.ViewChangelogComponentKey, action);
        var adapter = new AdapterProbe(Provider, new LibraryFilter(loadout, game));
        var messages = new List<ViewChangelogMessage>();
        using var subscription = adapter.MessageSubject.Subscribe(message => message.Switch(_ => { }, _ => { }, _ => { }, messages.Add, _ => { }, _ => { }, _ => { }, _ => { }));

        await OnUi(() =>
        {
            adapter.Hook(row);
            action.CommandViewChangelog.Execute(Unit.Default);
            return Task.CompletedTask;
        });

        messages.Should().ContainSingle().Which.Ids.Should().Equal(LibraryItemId.From(item));
    }

    [Fact]
    public async Task OpeningTheModPageOfANexusLoadoutItem_OpensTheNexusPage()
    {
        var (item, modId) = await AddNexusItem();
        var (game, _) = TestGame.NewPair();
        var loadout = await TestGame.AddLoadout(Connection, game);
        var linked = await Link(loadout, item);
        var os = Substitute.For<IOSInterop>();

        LoadoutViewModel.HandleOpenModPageFor([linked], Connection, os, Resolver, CancellationToken.None);

        os.Received(1).OpenUri(NexusModsUrlBuilder.GetModUri(GameDomain.From("eldenring"), ModId.From(modId)));
    }

    [Fact]
    public async Task OpeningTheModPageOfALocalFileLoadoutItem_OpensNothing()
    {
        var item = await AddLocalItem();
        var (game, _) = TestGame.NewPair();
        var loadout = await TestGame.AddLoadout(Connection, game);
        var linked = await Link(loadout, item);
        var os = Substitute.For<IOSInterop>();

        LoadoutViewModel.HandleOpenModPageFor([linked], Connection, os, Resolver, CancellationToken.None);

        os.DidNotReceive().OpenUri(Arg.Any<Uri>());
    }
}
