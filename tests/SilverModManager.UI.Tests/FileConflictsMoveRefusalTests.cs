using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Resources;
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

    private static readonly FileConflictsComponents.NeighbourIds Neighbours = new(EntityId.From(9), EntityId.From(11));

    private static StandardDialogResult Pressed(ButtonDefinitionId id) => new() { ButtonId = id };

    /// <summary>Shows what the refusal dialog was given and answers it with <paramref name="answer"/>.</summary>
    private static IWindowManager WindowsAnswering(ButtonDefinitionId answer, List<Dialog> shown)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Do<IDialog>(d => shown.Add((Dialog)d)), Arg.Any<DialogWindowType>())
            .Returns(Task.FromResult(Pressed(answer)));
        return windows;
    }

    [Fact]
    public async Task AMoveARuleStopsOffersCancelOrRemovingTheRule()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Refused Move");
        var rule = await SeedRule(loadout.LoadoutId, "Winning Mod", "Losing Mod");

        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.RuleViolation(rule)));
        var shown = new List<Dialog>();
        var vm = new FileConflictsViewModel(new FakedServices(Provider, loadouts), WindowsAnswering(ButtonDefinitionId.Cancel, shown), loadout.LoadoutId);

        await vm.Move(EntityId.From(10), Neighbours, System.ComponentModel.ListSortDirection.Ascending, moveUp: false);

        var dialog = shown.Should().ContainSingle().Subject;
        dialog.ViewModel.WindowTitle.Should().Be(Language.FileConflicts_Refused_Title);
        dialog.ViewModel.ButtonDefinitions.Select(b => b.Text).Should()
            .Equal(DialogStandardButtons.Cancel.Text, Language.FileConflicts_Refused_RemoveAndMove);
        await loadouts.DidNotReceive().RemoveGroupRule(Arg.Any<LoadoutGroupRuleId>());
        await loadouts.Received(1).ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>());
    }

    [Fact]
    public async Task RemovingTheRuleDeletesItAndRepeatsTheMove()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Refused Then Removed");
        var rule = await SeedRule(loadout.LoadoutId, "Winning Mod", "Losing Mod");

        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.RuleViolation(rule)), new ValueTask<ConflictMoveResult>(ConflictMoveResult.Moved));
        var shown = new List<Dialog>();
        var vm = new FileConflictsViewModel(new FakedServices(Provider, loadouts), WindowsAnswering(ButtonDefinitionId.Accept, shown), loadout.LoadoutId);

        await vm.Move(EntityId.From(10), Neighbours, System.ComponentModel.ListSortDirection.Ascending, moveUp: false);

        shown.Should().ContainSingle();
        await loadouts.Received(1).RemoveGroupRule(rule.LoadoutGroupRuleId);
        await loadouts.Received(2).ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>());
    }

    [Fact]
    public async Task EveryKindOfMoveGoesThroughTheSameRefusalHandling()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Refused Drag");
        var rule = await SeedRule(loadout.LoadoutId, "Winning Mod", "Losing Mod");

        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.LoseAllFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.RuleViolation(rule)), new ValueTask<ConflictMoveResult>(ConflictMoveResult.Moved));
        var shown = new List<Dialog>();
        var vm = new FileConflictsViewModel(new FakedServices(Provider, loadouts), WindowsAnswering(ButtonDefinitionId.Accept, shown), loadout.LoadoutId);

        await vm.RunMove(() => loadouts.LoseAllFileConflicts([]));

        shown.Should().ContainSingle();
        await loadouts.Received(1).RemoveGroupRule(rule.LoadoutGroupRuleId);
        await loadouts.Received(2).LoseAllFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>());
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
