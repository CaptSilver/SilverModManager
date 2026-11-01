using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using R3;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Networking.NexusWebApi.UpdateFilters;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Library;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The hide-updates menu item asks the item's source what it shows and what to do, so its click works for any source.
/// </summary>
public class HideUpdatesActionTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class AdapterProbe(IServiceProvider provider, LibraryFilter filter) : LibraryTreeDataGridAdapter(provider, filter)
    {
        public void Hook(CompositeItemModel<EntityId> model) => BeforeModelActivationHook(model);
    }

    private async Task<LibraryItem.ReadOnly> AddItem()
    {
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes("hide " + Guid.NewGuid());
        var file = new LibraryFile.New(tx, out var id)
        {
            FileName = "hide.zip",
            Hash = bytes.xxHash3(),
            Size = Size.From((ulong)bytes.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "hide.zip" },
        };
        var result = await tx.Commit();
        return LibraryItem.Load(Connection.Db, result.Remap(file).Id);
    }

    private async Task Toggle(FakeModSource source, params LibraryItem.ReadOnly[] items)
        => await HideUpdatesToggle.Apply(Connection.Db, new ModSourceResolver([source]), items.Select(static item => item.LibraryItemId), CancellationToken.None);

    [Fact]
    public async Task AClickOnARowWhoseUpdatesAreShown_HidesThem()
    {
        var item = await AddItem();
        var source = new FakeModSource();
        source.OwnedItems.Add(item.Id);
        source.HideState.OnNext(new HideUpdatesState(IsHidden: false, ItemCount: 1, IsEnabled: true, IsVisible: true));

        await Toggle(source, item);

        source.HideRequests.Should().ContainSingle().Which.Hidden.Should().BeTrue();
    }

    [Fact]
    public async Task AClickOnARowWhoseUpdatesAreHidden_ShowsThem()
    {
        var item = await AddItem();
        var source = new FakeModSource();
        source.OwnedItems.Add(item.Id);
        source.HideState.OnNext(new HideUpdatesState(IsHidden: true, ItemCount: 1, IsEnabled: true, IsVisible: true));

        await Toggle(source, item);

        source.HideRequests.Should().ContainSingle().Which.Hidden.Should().BeFalse();
    }

    [Fact]
    public async Task AClickOnAParentRow_PassesAllItsItemsToTheirSourceTogether()
    {
        var first = await AddItem();
        var second = await AddItem();
        var source = new FakeModSource();
        source.OwnedItems.Add(first.Id);
        source.OwnedItems.Add(second.Id);

        await Toggle(source, first, second);

        source.HideRequests.Should().ContainSingle().Which.Items.Select(static item => item.Id).Should().BeEquivalentTo([first.Id, second.Id]);
    }

    [Fact]
    public async Task AClickOnAnItemNoSourceOwns_DoesNothing()
    {
        var item = await AddItem();
        var source = new FakeModSource();

        await Toggle(source, item);

        source.HideRequests.Should().BeEmpty();
    }

    [Fact]
    public async Task ARowsHideActionTakesItsStateAndLabelFromTheSource()
    {
        var item = await AddItem();
        var source = new FakeModSource { HideLabel = "Hold back" };
        source.OwnedItems.Add(item.Id);
        source.HideState.OnNext(new HideUpdatesState(IsHidden: false, ItemCount: 3, IsEnabled: true, IsVisible: true));
        var row = new CompositeItemModel<EntityId>(item.Id);

        LibraryDataProviderHelper.AddHideUpdatesActionComponent(row, source, [item]);
        var action = row.Get<LibraryComponents.HideUpdatesAction>(LibraryColumns.Actions.HideUpdatesComponentKey);
        await EventuallyOnUi(() =>
        {
            action.ButtonText.Value.Should().Be("Hold back (3)");
            action.IsEnabled.Value.Should().BeTrue();
            action.IsVisible.Value.Should().BeTrue();
        });

        source.HideState.OnNext(new HideUpdatesState(IsHidden: true, ItemCount: 3, IsEnabled: false, IsVisible: false));

        await EventuallyOnUi(() =>
        {
            action.ButtonText.Value.Should().Be("Show fake (3)");
            action.IsEnabled.Value.Should().BeFalse();
            action.IsVisible.Value.Should().BeFalse();
        });
    }

    // The real Nexus source and filter service. No update check has run, so the source has no update to report for the
    // item; the click must still go through rather than wait for one.
    [Fact]
    public async Task AClickOnANexusItem_HidesEveryVersionOfItsFileThroughTheFilterService()
    {
        var modId = 93000 + (uint)Random.Shared.Next(1, 900000);
        var game = NexusModsGameId.From(4333);
        using var tx = Connection.BeginTransaction();
        var bytes = System.Text.Encoding.UTF8.GetBytes("nexus hide " + modId);
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
        var current = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(modId), game),
            Name = "mod.zip",
            Version = "1.0",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime,
            ModPageId = page,
        };
        var newer = new NexusModsFileMetadata.New(tx)
        {
            Uid = new FileUid(FileId.From(modId + 1), game),
            Name = "mod.zip",
            Version = "2.0",
            UploadedAt = DateTimeOffset.UnixEpoch.UtcDateTime.AddDays(1),
            ModPageId = page,
        };
        tx.Add(id, NexusModsLibraryItem.FileMetadataId, current.Id);
        tx.Add(id, NexusModsLibraryItem.ModPageMetadataId, page.Id);
        var result = await tx.Commit();
        var filters = Provider.GetRequiredService<IModUpdateFilterService>();
        var newerUid = new FileUid(FileId.From(modId + 1), game);
        filters.IsFileHidden(newerUid).Should().BeFalse();

        await HideUpdatesToggle.Apply(Connection.Db, Provider.GetRequiredService<ModSourceResolver>(), [LibraryItemId.From(result.Remap(file).Id)], CancellationToken.None);

        filters.IsFileHidden(newerUid).Should().BeTrue();
    }

    [Fact]
    public async Task AHideUpdatesClick_CarriesTheRowsLibraryItemIds()
    {
        var item = await AddItem();
        var (game, _) = TestGame.NewPair();
        var loadout = await TestGame.AddLoadout(Connection, game);
        var row = new CompositeItemModel<EntityId>(item.Id);
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(item.LibraryItemId));
        var action = new LibraryComponents.HideUpdatesAction(Observable.Return(false), Observable.Return(1), Observable.Return(true), Observable.Return(true));
        row.Add(LibraryColumns.Actions.HideUpdatesComponentKey, action);
        var adapter = new AdapterProbe(Provider, new LibraryFilter(loadout, game));
        var messages = new List<HideUpdatesMessage>();
        using var subscription = adapter.MessageSubject.Subscribe(message => message.Switch(_ => { }, _ => { }, _ => { }, _ => { }, _ => { }, messages.Add, _ => { }, _ => { }));

        await OnUi(() =>
        {
            adapter.Hook(row);
            action.CommandHideUpdates.Execute(Unit.Default);
            return Task.CompletedTask;
        });

        messages.Should().ContainSingle().Which.Ids.Should().Equal(item.LibraryItemId);
    }
}
