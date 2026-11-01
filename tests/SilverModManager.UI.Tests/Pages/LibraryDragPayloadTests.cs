using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Controls.DragAndDrop;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.Wabbajack.Models;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>What dragging rows of the Library page carries: the installable library items, for the loadout the page is of.</summary>
public class LibraryDragPayloadTests(IServiceProvider provider) : AUiTest(provider)
{
    private IDisposable? _activation;

    public override async ValueTask DisposeAsync()
    {
        _activation?.Dispose();
        await base.DisposeAsync();
    }

    private async Task<(LibraryViewModel Vm, Loadout.ReadOnly Loadout)> CreateViewModel()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Library drag " + Guid.NewGuid());
        var vm = await OnUi(() => Task.FromResult(new LibraryViewModel(
            Substitute.For<IWindowManager>(),
            new ServicesWithSources(Provider).With(Substitute.For<IWindowNotificationService>()),
            Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            loadout.LoadoutId)));
        _activation = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        return (vm, loadout);
    }

    private async Task<LibraryItemId> SeedItem(string name, bool isModlist)
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
        if (isModlist)
        {
            _ = new WabbajackModlistLibraryFile.New(tx, id)
            {
                LibraryFile = file, Name = name, Author = "Someone", Version = "1.0", GameType = "NoSuchGame",
                ArchiveCount = 0, ArchiveSize = Size.Zero, InstallSize = Size.Zero, IsNsfw = false,
            };
        }
        var result = await tx.Commit();
        return LibraryItemId.From(result[id]);
    }

    private static CompositeItemModel<EntityId> Row(params LibraryItemId[] items)
    {
        var row = new CompositeItemModel<EntityId>(items[0].Value);
        var changes = new ChangeSet<LibraryItemId, EntityId>(items.Select(item => new Change<LibraryItemId, EntityId>(ChangeReason.Add, item.Value, item)));
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey,
            new LibraryComponents.LibraryItemIds(System.Reactive.Linq.Observable.Return<IChangeSet<LibraryItemId, EntityId>>(changes)));
        return row;
    }

    [Fact]
    public async Task RowsWithTwoMods_AndAModlist_CarryTheTwoModsForThisLoadout()
    {
        var (vm, loadout) = await CreateViewModel();
        var first = await SeedItem("first.7z", isModlist: false);
        var list = await SeedItem("list.wabbajack", isModlist: true);
        var second = await SeedItem("second.7z", isModlist: false);

        var payload = vm.DragPayloadFor([Row(first), Row(list), Row(second)]);

        payload.Should().BeOfType<DragPayload.LibraryItems>().Which.Should().BeEquivalentTo(
            new { Loadout = loadout.LoadoutId, Items = new[] { first, second } },
            options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task OneRowOverSeveralItems_CarriesEachOnce()
    {
        var (vm, _) = await CreateViewModel();
        var first = await SeedItem("first.7z", isModlist: false);
        var second = await SeedItem("second.7z", isModlist: false);

        var payload = vm.DragPayloadFor([Row(first, second), Row(second)]);

        payload.Should().BeOfType<DragPayload.LibraryItems>().Which.Items.Should().Equal(first, second);
    }

    [Fact]
    public async Task OnlyModlists_CarryNothing()
    {
        var (vm, _) = await CreateViewModel();
        var list = await SeedItem("list.wabbajack", isModlist: true);

        vm.DragPayloadFor([Row(list)]).Should().BeNull();
    }

    [Fact]
    public async Task NoRows_CarryNothing()
    {
        var (vm, _) = await CreateViewModel();

        vm.DragPayloadFor([]).Should().BeNull();
    }
}
