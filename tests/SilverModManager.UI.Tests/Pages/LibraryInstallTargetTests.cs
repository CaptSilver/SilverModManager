using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The installation targets are filled in the background once the page activates, so the Install button can be
/// pressed while there are none yet. The commands stay disabled until one has arrived, on every activation.
/// </summary>
public class LibraryInstallTargetTests(IServiceProvider provider) : AUiTest(provider)
{
    private IDisposable? _activation;

    public override async ValueTask DisposeAsync()
    {
        _activation?.Dispose();
        await base.DisposeAsync();
    }

    [Fact]
    public async Task InstallIsDisabledUntilTheTargetsHaveArrivedThenInstallsIntoTheFirst()
    {
        var (vm, manager, loadout, collection, _) = await Arrange();
        vm.InstallationTargets.Should().BeEmpty("the page has not activated, so nothing has filled them");
        await OnUi(() => { vm.InstallSelectedItemsCommand.CanExecute().Should().BeFalse(); return Task.CompletedTask; });

        _activation = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        await EventuallyOnUi(() => vm.InstallSelectedItemsCommand.CanExecute().Should().BeTrue());
        await OnUi(() => { vm.InstallSelectedItemsCommand.Execute(R3.Unit.Default); return Task.CompletedTask; });

        await Eventually(() => _ = manager.Received(1).InstallItems(
            Arg.Any<IReadOnlyList<LibraryItem.ReadOnly>>(),
            loadout.LoadoutId,
            Arg.Is<Optional<LoadoutItemGroupId>>(parent => parent.HasValue && parent.Value.Value == collection.Id)));
    }

    [Fact]
    public async Task InstallIsDisabledAgainWhileTheTargetsAreRebuiltOnReactivation()
    {
        var (vm, manager, loadout, collection, _) = await Arrange();
        _activation = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        await EventuallyOnUi(() => vm.InstallSelectedItemsCommand.CanExecute().Should().BeTrue());
        _activation.Dispose();

        // Reactivating must not leave the flag from the last activation claiming targets that are not bound yet. The
        // new targets are bound by a post to the UI thread, which cannot run inside this job, so the check is exact.
        _activation = await OnUi(() =>
        {
            var activation = vm.Activator.Activate();
            vm.InstallSelectedItemsCommand.CanExecute().Should().BeFalse();
            return Task.FromResult(activation);
        });
        await EventuallyOnUi(() => vm.InstallSelectedItemsCommand.CanExecute().Should().BeTrue(), TimeSpan.FromSeconds(30));
        await OnUi(() => { vm.InstallSelectedItemsCommand.Execute(R3.Unit.Default); return Task.CompletedTask; });

        await Eventually(() => _ = manager.Received(1).InstallItems(
            Arg.Any<IReadOnlyList<LibraryItem.ReadOnly>>(),
            loadout.LoadoutId,
            Arg.Is<Optional<LoadoutItemGroupId>>(parent => parent.HasValue && parent.Value.Value == collection.Id)));
    }

    [Fact]
    public async Task ARowInstallSentBeforeTheTargetsArriveWaitsAndThenInstallsIntoTheFirst()
    {
        var (vm, manager, loadout, collection, item) = await Arrange();

        // The message and the activation share one UI job, so the background query cannot have been bound yet.
        _activation = await OnUi(() =>
        {
            var activation = vm.Activator.Activate();
            vm.InstallationTargets.Should().BeEmpty();
            vm.Adapter.MessageSubject.OnNext(new InstallMessage([item.LibraryItemId]));
            return Task.FromResult(activation);
        });

        await Eventually(() => _ = manager.Received(1).InstallItem(
            Arg.Is<LibraryItem.ReadOnly>(x => x.LibraryItemId == item.LibraryItemId),
            loadout.LoadoutId,
            Arg.Is<Optional<LoadoutItemGroupId>>(parent => parent.HasValue && parent.Value.Value == collection.Id),
            Arg.Any<ILibraryItemInstaller?>(),
            Arg.Any<ILibraryItemInstaller?>(),
            Arg.Any<bool>()));
    }

    private async Task<(LibraryViewModel Vm, ILoadoutManager Manager, Loadout.ReadOnly Loadout, CollectionGroup.ReadOnly Collection, LibraryItem.ReadOnly Item)> Arrange()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Install before targets " + Guid.NewGuid());
        var item = await SeedLibraryItem("early.7z");
        var collection = await SeedCollection(loadout, "Late target");
        var manager = Substitute.For<ILoadoutManager>();
        var vm = await OnUi(() => Task.FromResult(new LibraryViewModel(
            Substitute.For<IWindowManager>(),
            new ServicesWithSources(Provider).With(manager).With(Substitute.For<IErrorPresenter>()).With(Substitute.For<IWindowNotificationService>()),
            Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            loadout.LoadoutId)));
        var row = new CompositeItemModel<EntityId>(item.Id);
        row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(item.LibraryItemId));
        await OnUi(() => { vm.Adapter.SelectedModels.Add(row); return Task.CompletedTask; });
        return (vm, manager, loadout, collection, item);
    }

    private async Task<LibraryItem.ReadOnly> SeedLibraryItem(string name)
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
        var result = await tx.Commit();
        return LibraryItem.Load(result.Db, result[file.Id]);
    }

    private async Task<CollectionGroup.ReadOnly> SeedCollection(Loadout.ReadOnly loadout, string name)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
            },
        };
        var result = await tx.Commit();
        return CollectionGroup.Load(result.Db, result[id]);
    }
}
