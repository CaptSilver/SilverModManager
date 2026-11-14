using System.ComponentModel;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Controls;
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

/// <summary>
/// Collections are ordered with the mods on the File Conflicts page: a collection row moves all of its mods at once,
/// a mod dragged on its own leaves the rest behind, and a rule can name a collection.
/// </summary>
public class FileConflictsCollectionTests(IServiceProvider provider) : AUiTest(provider)
{
    // The view model reads keyed services (image pipelines), which only a keyed provider serves.
    private sealed class FakedServices(IServiceProvider inner, ILoadoutManager loadouts) : IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(ILoadoutManager) ? loadouts : inner.GetService(serviceType);
        public object? GetKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetKeyedService(serviceType, serviceKey);
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetRequiredKeyedService(serviceType, serviceKey);
    }

    private sealed record Seeded(Loadout.ReadOnly Loadout, Dictionary<string, LoadoutItemGroupPriorityId> Rows, Dictionary<string, LoadoutItemGroupId> Groups, Dictionary<string, EntityId> Collections);

    /// <summary>
    /// Mods in ascending priority, each optionally in a collection ("Mod name", "Collection name"). Row keys are by
    /// mod name, collection keys by collection name.
    /// </summary>
    private async Task<Seeded> Seed(string loadoutName, params (string Mod, string? Collection)[] mods)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, loadoutName);
        using var tx = Connection.BeginTransaction();

        var collectionIds = new Dictionary<string, CollectionGroupId>();
        foreach (var name in mods.Select(m => m.Collection).OfType<string>().Distinct())
        {
            _ = new CollectionGroup.New(tx, out var id)
            {
                IsReadOnly = true,
                LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
                {
                    IsGroup = true,
                    LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout.LoadoutId },
                },
            };
            collectionIds[name] = id;
        }

        var added = mods.Select((m, i) =>
        {
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = m.Mod },
            };
            if (m.Collection is { } collection) tx.Add(id, LoadoutItem.Parent, collectionIds[collection]);
            var row = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = group.Id, Priority = ConflictPriority.From((ulong)i + 1) };
            return (m.Mod, Group: group.Id, Row: row.Id);
        }).ToArray();

        var result = await tx.Commit();
        return new Seeded(
            loadout,
            added.ToDictionary(a => a.Mod, a => LoadoutItemGroupPriorityId.From(result[a.Row].Value)),
            added.ToDictionary(a => a.Mod, a => LoadoutItemGroupId.From(result[a.Group].Value)),
            collectionIds.ToDictionary(c => c.Key, c => result[c.Value]));
    }

    private static IWindowManager WindowsAnswering(ButtonDefinitionId answer, List<Dialog> shown)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Do<IDialog>(d => shown.Add((Dialog)d)), Arg.Any<DialogWindowType>())
            .Returns(Task.FromResult(new StandardDialogResult { ButtonId = answer }));
        return windows;
    }

    private static ILoadoutManager MovesAllowed()
    {
        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.Moved));
        loadouts.LoseAllFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.Moved));
        return loadouts;
    }

    private FileConflictsViewModel Page(Seeded seeded, ILoadoutManager loadouts, IWindowManager? windows = null)
        => new(new FakedServices(Provider, loadouts), windows ?? Substitute.For<IWindowManager>(), seeded.Loadout.LoadoutId);

    [Fact]
    public async Task DraggingACollectionMovesAllItsModsInTheirOrderOverTheModItLandsOn()
    {
        var s = await Seed("Drag Collection", ("Solo 1", null), ("Coll 1", "Pack"), ("Coll 2", "Pack"), ("Solo 2", null));
        var loadouts = MovesAllowed();

        // Below Solo 2 on an ascending list: above it in priority.
        await Page(s, loadouts).Drop([s.Collections["Pack"]], s.Rows["Solo 2"].Value, visuallyAbove: false);

        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["Coll 1"], s.Rows["Coll 2"] })),
            s.Rows["Solo 2"]);
    }

    [Fact]
    public async Task DraggingACollectionToTheBottomLosesAllConflictsForItsMods()
    {
        var s = await Seed("Drag To Bottom", ("Solo 1", null), ("Coll 1", "Pack"), ("Coll 2", "Pack"));
        var loadouts = MovesAllowed();

        // Above the lowest row on an ascending list: below everything in priority.
        await Page(s, loadouts).Drop([s.Collections["Pack"]], s.Rows["Solo 1"].Value, visuallyAbove: true);

        await loadouts.Received(1).LoseAllFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["Coll 1"], s.Rows["Coll 2"] })));
    }

    [Fact]
    public async Task OnADescendingListTheTopIsTheHighestPriority()
    {
        var s = await Seed("Drag Descending", ("Solo 1", null), ("Coll 1", "Pack"), ("Solo 2", null));
        var loadouts = MovesAllowed();
        var page = Page(s, loadouts);
        page.SwitchSortDirectionCommand.Execute(R3.Unit.Default);

        // Above Solo 2 on screen is above it in priority, so the collection lands over Solo 2 itself.
        await page.Drop([s.Collections["Pack"]], s.Rows["Solo 2"].Value, visuallyAbove: true);

        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["Coll 1"] })),
            s.Rows["Solo 2"]);
    }

    [Fact]
    public async Task AModDraggedOnItsOwnLeavesTheRestOfItsCollectionBehind()
    {
        var s = await Seed("Split Out", ("Coll 1", "Pack"), ("Coll 2", "Pack"), ("Solo", null));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Drop([s.Rows["Coll 1"].Value], s.Rows["Solo"].Value, visuallyAbove: false);

        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["Coll 1"] })),
            s.Rows["Solo"]);
    }

    [Fact]
    public async Task ADragThatBreaksARuleNamingTheCollectionIsRefusedWithTheReason()
    {
        var s = await Seed("Refused Collection Drag", ("Solo", null), ("Coll 1", "Pack"));
        var rule = await StoreRule(s.Loadout.LoadoutId, s.Collections["Pack"], s.Groups["Solo"]);
        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.LoseAllFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.RuleViolation(rule)));
        var shown = new List<Dialog>();
        var page = Page(s, loadouts, WindowsAnswering(ButtonDefinitionId.Cancel, shown));

        await page.Drop([s.Collections["Pack"]], s.Rows["Solo"].Value, visuallyAbove: true);

        var dialog = shown.Should().ContainSingle().Subject;
        dialog.ViewModel.WindowTitle.Should().Be(Language.FileConflicts_Refused_Title);
        await loadouts.DidNotReceive().RemoveGroupRule(Arg.Any<LoadoutGroupRuleId>());
    }

    [Fact]
    public async Task MovingACollectionDownStepsPastTheModsOfItsOwnAndLandsOverTheRowBelowThem()
    {
        var s = await Seed("Move Collection", ("Solo 1", null), ("Coll 1", "Pack"), ("Coll 2", "Pack"), ("Solo 2", null));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Reorderable.MoveBy(s.Collections["Pack"], +1, CancellationToken.None);

        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["Coll 1"], s.Rows["Coll 2"] })),
            s.Rows["Solo 2"]);
    }

    [Fact]
    public async Task MovingACollectionUpPastAnotherCollectionPassesTheWholeOfIt()
    {
        var s = await Seed("Move Past Collection", ("A 1", "Pack A"), ("A 2", "Pack A"), ("B 1", "Pack B"));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Reorderable.MoveBy(s.Collections["Pack B"], -1, CancellationToken.None);

        // Pack B ends below all of Pack A, which is the bottom of the priority order.
        await loadouts.Received(1).LoseAllFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["B 1"] })));
    }

    [Fact]
    public async Task MovingAModStillMovesOnlyThatMod()
    {
        var s = await Seed("Move Mod", ("Coll 1", "Pack"), ("Coll 2", "Pack"), ("Solo", null));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Reorderable.MoveBy(s.Rows["Solo"].Value, -1, CancellationToken.None);

        // Solo goes between the two mods of Pack: over Coll 1, under Coll 2.
        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["Solo"] })),
            s.Rows["Coll 1"]);
    }

    [Fact]
    public async Task MoveByTwoDownMakesTheMovedUnitWinOverTheRowTwoBelow()
    {
        var s = await Seed("Move By Two", ("A", null), ("B", null), ("C", null), ("D", null), ("E", null));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Reorderable.MoveBy(s.Rows["B"].Value, +2, CancellationToken.None);

        // Down the screen is toward the winning end of an ascending list, so B lands over D.
        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["B"] })),
            s.Rows["D"]);
    }

    [Fact]
    public async Task MovingPastTheEndLandsAtTheWinningBottom()
    {
        var s = await Seed("Move Past Bottom", ("A", null), ("B", null), ("C", null));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Reorderable.MoveBy(s.Rows["A"].Value, +10, CancellationToken.None);

        await loadouts.Received(1).ResolveFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["A"] })),
            s.Rows["C"]);
    }

    [Fact]
    public async Task MovingPastTheTopLosesEveryConflict()
    {
        var s = await Seed("Move Past Top", ("A", null), ("B", null), ("C", null));
        var loadouts = MovesAllowed();

        await Page(s, loadouts).Reorderable.MoveBy(s.Rows["C"].Value, -10, CancellationToken.None);

        await loadouts.Received(1).LoseAllFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["C"] })));
    }

    [Fact]
    public async Task OnADescendingPageDownTheScreenIsTowardTheLowestPriority()
    {
        var s = await Seed("Move Descending", ("A", null), ("B", null), ("C", null));
        var loadouts = MovesAllowed();
        var page = Page(s, loadouts);
        page.SwitchSortDirectionCommand.Execute(R3.Unit.Default);

        // The page shows C, B, A: B moves down past A, to the bottom of the priority order.
        await page.Reorderable.MoveBy(s.Rows["B"].Value, +1, CancellationToken.None);

        await loadouts.Received(1).LoseAllFileConflicts(
            Arg.Is<LoadoutItemGroupPriorityId[]>(ids => ids.SequenceEqual(new[] { s.Rows["B"] })));
    }

    [Fact]
    public async Task IndexAndCountAreInDisplayedOrder()
    {
        var s = await Seed("Displayed Index", ("A", null), ("B", null), ("C", null), ("D", "Pack"), ("E", "Pack"));
        var page = Page(s, MovesAllowed());

        page.Reorderable.IndexOf(s.Rows["B"].Value).Should().Be(1);
        page.Reorderable.CountFor(s.Rows["B"].Value).Should().Be(5, "every mod is a row to move among");
        page.Reorderable.IndexOf(s.Collections["Pack"]).Should().Be(3, "a collection moves among the loose mods and other collections");
        page.Reorderable.CountFor(s.Collections["Pack"]).Should().Be(4);

        page.SwitchSortDirectionCommand.Execute(R3.Unit.Default);

        page.Reorderable.IndexOf(s.Rows["B"].Value).Should().Be(3);
        page.Reorderable.IndexOf(s.Collections["Pack"]).Should().Be(0);
    }

    [Fact]
    public async Task AMoveARuleStopsIsNotAppliedAndTheCallerIsTold()
    {
        var s = await Seed("Move Refused", ("A", null), ("B", null));
        var rule = await StoreRule(s.Loadout.LoadoutId, s.Groups["A"], s.Groups["B"]);
        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.ResolveFileConflicts(Arg.Any<LoadoutItemGroupPriorityId[]>(), Arg.Any<LoadoutItemGroupPriorityId>())
            .Returns(new ValueTask<ConflictMoveResult>(ConflictMoveResult.RuleViolation(rule)));
        var page = Page(s, loadouts, WindowsAnswering(ButtonDefinitionId.Cancel, []));

        var move = () => page.Reorderable.MoveBy(s.Rows["A"].Value, +1, CancellationToken.None);

        await move.Should().ThrowAsync<InvalidOperationException>();
        (await page.Reorderable.TryMoveBy(s.Rows["A"].Value, +1, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ARowThatIsNotInThePriorityOrderCannotBeMoved()
    {
        var s = await Seed("Move Stale", ("A", null), ("B", null));
        var loadouts = MovesAllowed();

        var move = () => Page(s, loadouts).Reorderable.MoveBy(EntityId.From(999999), +1, CancellationToken.None);

        await move.Should().ThrowAsync<InvalidOperationException>();
        await loadouts.DidNotReceiveWithAnyArgs().ResolveFileConflicts(default!, default);
    }

    [Fact]
    public async Task TheRulesActionOnACollectionOpensTheEditorForTheCollection()
    {
        var s = await Seed("Rules For Collection", ("Solo", null), ("Coll 1", "Pack"), ("Other 1", "Other Pack"));
        var windows = Substitute.For<IWindowManager>();
        IDialog? shown = null;
        _ = windows.ShowDialog(Arg.Do<IDialog>(d => shown = d), DialogWindowType.Modeless);

        await Page(s, Provider.GetRequiredService<ILoadoutManager>(), windows)
            .OpenRules(new FileConflictsTreeDataGridAdapter.RulesMessage(s.Collections["Pack"]));

        var dialog = shown.Should().BeOfType<Dialog>().Subject;
        dialog.ViewModel.WindowTitle.Should().Be(string.Format(Language.FileConflicts_Rules_Title, "Pack"));
        var editor = dialog.ViewModel.ContentViewModel.Should().BeAssignableTo<IFileConflictRulesViewModel>().Subject;
        editor.Candidates.Select(c => c.Name).Should().BeEquivalentTo("Solo", "Other 1", "Other Pack");
    }

    [Fact]
    public async Task AModsEditorOffersOtherCollectionsButNotItsOwn()
    {
        var s = await Seed("Rules Offer Collections", ("Solo", null), ("Coll 1", "Pack"), ("Other 1", "Other Pack"));

        var editor = new FileConflictRulesViewModel(Connection, Provider.GetRequiredService<ILoadoutManager>(), s.Loadout.LoadoutId, s.Groups["Coll 1"]);

        editor.Candidates.Select(c => c.Name).Should().BeEquivalentTo("Solo", "Other 1", "Other Pack");
    }

    [Fact]
    public async Task ARuleAddedFromACollectionsEditorNamesTheCollectionAndIsListedFromBothSides()
    {
        var s = await Seed("Rules On Collection", ("Solo", null), ("Coll 1", "Pack"), ("Coll 2", "Pack"));
        var loadouts = Provider.GetRequiredService<ILoadoutManager>();
        var packEditor = new FileConflictRulesViewModel(Connection, loadouts, s.Loadout.LoadoutId, LoadoutItemGroupId.From(s.Collections["Pack"]));

        await packEditor.AddRule(s.Groups["Solo"], GroupRuleKind.Before);

        packEditor.Error.Should().BeNull();
        packEditor.Rows.Should().ContainSingle().Which.Text.Should().Be(string.Format(Language.FileConflicts_Rules_Loses, "Solo"));
        var soloEditor = new FileConflictRulesViewModel(Connection, loadouts, s.Loadout.LoadoutId, s.Groups["Solo"]);
        soloEditor.Rows.Should().ContainSingle().Which.Text.Should().Be(string.Format(Language.FileConflicts_Rules_Wins, "Pack"));
        var order = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, s.Loadout.LoadoutId).OrderBy(p => p.Priority).Select(p => p.Target.AsLoadoutItem().Name).ToList();
        order.IndexOf("Solo").Should().BeGreaterThan(order.IndexOf("Coll 2"), "the collection's mods lose to Solo");
    }

    // The grid's rows only exist once a view realizes them; the source's items are the roots in the order they show.
    private static IEnumerable<CompositeItemModel<EntityId>> Items(FileConflictsViewModel page)
        => page.TreeDataGridAdapter.Source.Value is HierarchicalTreeDataGridSource<CompositeItemModel<EntityId>> source ? source.Items : [];

    private static string[] ShownNames(FileConflictsViewModel page) => Items(page)
        .Select(model => model.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value)
        .ToArray();

    [Fact]
    public async Task TheListShowsEachCollectionAsARowHeadingItsMods()
    {
        var s = await Seed("List Headings", ("Solo 1", null), ("Coll 1", "Pack"), ("Coll 2", "Pack"), ("Solo 2", null));
        var page = Page(s, Provider.GetRequiredService<ILoadoutManager>());
        using var _ = page.Activator.Activate();

        await EventuallyOnUi(() => ShownNames(page).Should().Equal("Solo 1", "Pack", "Coll 1", "Coll 2", "Solo 2"));

        // Descending puts the highest priority first, and the collection heads its highest mod.
        page.SwitchSortDirectionCommand.Execute(R3.Unit.Default);
        await EventuallyOnUi(() => ShownNames(page).Should().Equal("Solo 2", "Pack", "Coll 2", "Coll 1", "Solo 1"));
    }

    [Fact]
    public async Task MovingACollectionReordersTheListWithoutRebuildingIt()
    {
        // A Reset makes the grid throw its rows away and recreate them, which shows as the list blinking after a drop.
        var s = await Seed("List No Reset", ("Solo 1", null), ("Coll 1", "Pack"), ("Coll 2", "Pack"), ("Solo 2", null));
        var loadouts = Provider.GetRequiredService<ILoadoutManager>();
        var page = Page(s, loadouts);
        using var _ = page.Activator.Activate();
        await EventuallyOnUi(() => ShownNames(page).Should().Equal("Solo 1", "Pack", "Coll 1", "Coll 2", "Solo 2"));
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        System.Collections.Specialized.NotifyCollectionChangedEventHandler record = (_, e) => actions.Add(e.Action);
        await OnUi(() => ((System.Collections.Specialized.INotifyCollectionChanged)Items(page)).CollectionChanged += record);

        var moved = await loadouts.WinAllFileConflicts([s.Rows["Coll 1"], s.Rows["Coll 2"]]);

        moved.IsRuleViolation.Should().BeFalse();
        await EventuallyOnUi(() => ShownNames(page).Should().Equal("Solo 1", "Solo 2", "Pack", "Coll 1", "Coll 2"));
        await OnUi(() => ((System.Collections.Specialized.INotifyCollectionChanged)Items(page)).CollectionChanged -= record);
        actions.Should().NotBeEmpty().And.NotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset);
    }

    private async Task<LoadoutGroupRule.ReadOnly> StoreRule(LoadoutId loadoutId, EntityId source, LoadoutItemGroupId other)
    {
        using var tx = Connection.BeginTransaction();
        var rule = new LoadoutGroupRule.New(tx) { LoadoutId = loadoutId, SourceId = LoadoutItemGroupId.From(source), OtherId = other, Kind = GroupRuleKind.After };
        var result = await tx.Commit();
        return result.Remap(rule);
    }
}
