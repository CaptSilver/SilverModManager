using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class FileConflictsMoveRefusalTests(IServiceProvider provider) : AUiTest(provider)
{
    // The view model reads keyed services (image pipelines), which only a keyed provider serves.
    private sealed class FakedServices(IServiceProvider inner, ILoadoutManager loadouts) : IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoadoutManager) ? loadouts : inner.GetService(serviceType);
        public object? GetKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetKeyedService(serviceType, serviceKey);
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetRequiredKeyedService(serviceType, serviceKey);
    }

    [Fact]
    public async Task AMoveARuleStopsShowsADialogNamingTheRule()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Refused Move");
        var rule = await SeedRule(loadout.LoadoutId, "Winning Mod", "Losing Mod");

        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.RuleViolation(rule)));
        var windows = Substitute.For<IWindowManager>();
        var vm = new FileConflictsViewModel(new FakedServices(Provider, loadouts), windows, loadout.LoadoutId);

        await vm.Move(EntityId.From(10), new FileConflictsComponents.NeighbourIds(EntityId.From(9), EntityId.From(11)),
            System.ComponentModel.ListSortDirection.Ascending, moveUp: false);

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
    }

    [Fact]
    public async Task AMoveThatWasDoneShowsNoDialog()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Done Move");
        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.Moved));
        var windows = Substitute.For<IWindowManager>();
        var vm = new FileConflictsViewModel(new FakedServices(Provider, loadouts), windows, loadout.LoadoutId);

        await vm.Move(EntityId.From(10), new FileConflictsComponents.NeighbourIds(EntityId.From(9), EntityId.From(11)),
            System.ComponentModel.ListSortDirection.Ascending, moveUp: false);

        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
    }

    private async Task<LoadoutGroupRule.ReadOnly> SeedRule(LoadoutId loadoutId, string source, string other)
    {
        using var tx = Connection.BeginTransaction();
        var sourceGroup = NewGroup(tx, loadoutId, source);
        var otherGroup = NewGroup(tx, loadoutId, other);
        var rule = new LoadoutGroupRule.New(tx) { LoadoutId = loadoutId, SourceId = sourceGroup, OtherId = otherGroup, Kind = GroupRuleKind.After };
        var result = await tx.Commit();
        return result.Remap(rule);
    }

    private static LoadoutItemGroupId NewGroup(ITransaction tx, LoadoutId loadoutId, string name)
    {
        var group = new LoadoutItemGroup.New(tx, out var id)
        {
            IsGroup = true,
            LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadoutId, Name = name },
        };
        return group.Id;
    }
}
