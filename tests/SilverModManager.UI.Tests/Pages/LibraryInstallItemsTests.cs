using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Library;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.LibraryPage;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Install button and a drop onto a collection install through the same <see cref="ILoadoutManager.InstallItems"/>
/// call, so a selection is installed whole or not at all.
/// </summary>
public class LibraryInstallItemsTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Harness
    {
        public required LibraryViewModel Vm { get; init; }
        public required ILoadoutManager Manager { get; init; }
        public required IErrorPresenter Errors { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public required LibraryItem.ReadOnly[] Items { get; init; }
        public required CollectionGroup.ReadOnly Collection { get; init; }
    }

    private IDisposable? _activation;

    public override async ValueTask DisposeAsync()
    {
        _activation?.Dispose();
        await base.DisposeAsync();
    }

    private async Task<Harness> CreateHarness(Action<ILoadoutManager>? arrange = null)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Install items " + Guid.NewGuid());
        var items = await SeedLibraryItems("first.7z", "second.7z", "third.7z");
        var collection = await SeedCollection(loadout, "Target collection");
        var manager = Substitute.For<ILoadoutManager>();
        arrange?.Invoke(manager);
        var errors = Substitute.For<IErrorPresenter>();
        var vm = await OnUi(() => Task.FromResult(new LibraryViewModel(
            Substitute.For<IWindowManager>(),
            new ServicesWithSources(Provider).With(manager).With(errors).With(Substitute.For<IWindowNotificationService>()),
            Provider.GetRequiredService<IGameDomainToGameIdMappingCache>(),
            loadout.LoadoutId)));
        _activation = await OnUi(() => Task.FromResult(vm.Activator.Activate()));
        await Eventually(() => vm.InstallationTargets.Should().ContainSingle(target => target.Id.Value == collection.Id));
        foreach (var item in items)
        {
            var row = new CompositeItemModel<EntityId>(item.Id);
            row.Add(LibraryColumns.Actions.LibraryItemIdsComponentKey, new LibraryComponents.LibraryItemIds(item.LibraryItemId));
            await OnUi(() => { vm.Adapter.SelectedModels.Add(row); return Task.CompletedTask; });
        }
        return new Harness { Vm = vm, Manager = manager, Errors = errors, Loadout = loadout, Items = items, Collection = collection };
    }

    private async Task<LibraryItem.ReadOnly[]> SeedLibraryItems(params string[] names)
    {
        using var tx = Connection.BeginTransaction();
        var added = names.Select(name =>
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(name + Guid.NewGuid());
            var file = new LibraryFile.New(tx, out var id)
            {
                FileName = name,
                Hash = bytes.xxHash3(),
                Size = Size.From((ulong)bytes.Length),
                LibraryItem = new LibraryItem.New(tx, id) { Name = name },
            };
            return file.Id;
        }).ToArray();
        var result = await tx.Commit();
        return added.Select(id => LibraryItem.Load(result.Db, result[id])).ToArray();
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

    [Fact]
    public async Task TheInstallButton_InstallsTheWholeSelectionInOneCall()
    {
        var harness = await CreateHarness();

        harness.Vm.InstallSelectedItemsCommand.Execute(R3.Unit.Default);

        await Eventually(() => _ = harness.Manager.Received(1).InstallItems(
            Arg.Is<IReadOnlyList<LibraryItem.ReadOnly>>(items => items.Select(item => item.Id).SequenceEqual(harness.Items.Select(item => item.Id))),
            harness.Loadout.LoadoutId,
            Arg.Is<Optional<LoadoutItemGroupId>>(parent => parent.HasValue && parent.Value.Value == harness.Collection.Id)));
        _ = harness.Manager.DidNotReceiveWithAnyArgs().InstallItem(default, default, default, default, default);
    }

    [Fact]
    public async Task WhenOneItemFails_NothingIsInstalledOneByOneAndTheFailureIsShown()
    {
        var failure = new InvalidOperationException("Couldn't install `second.7z`, so nothing was installed");
        var harness = await CreateHarness(manager => manager
            .InstallItems(Arg.Any<IReadOnlyList<LibraryItem.ReadOnly>>(), Arg.Any<LoadoutId>(), Arg.Any<Optional<LoadoutItemGroupId>>())
            .Returns(Task.FromException<LoadoutItemGroup.ReadOnly[]>(failure)));

        harness.Vm.InstallSelectedItemsCommand.Execute(R3.Unit.Default);

        await Eventually(() => _ = harness.Errors.Received(1).Report(failure, Arg.Any<string>()));
        _ = harness.Manager.DidNotReceiveWithAnyArgs().InstallItem(default, default, default, default, default);
    }

    [Fact]
    public async Task TheAdvancedInstallerKeepsInstallingOneItemAtATime()
    {
        var harness = await CreateHarness();

        harness.Vm.InstallSelectedItemsWithAdvancedInstallerCommand.Execute(R3.Unit.Default);

        await Eventually(() => _ = harness.Manager.ReceivedWithAnyArgs(3).InstallItem(default, default, default, default, default));
        _ = harness.Manager.DidNotReceiveWithAnyArgs().InstallItems(default!, default, default);
    }
}
