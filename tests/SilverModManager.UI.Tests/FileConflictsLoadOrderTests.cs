using System.Collections.Concurrent;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Models.TreeDataGrid;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.App.UI.Controls;
using SilverModManager.App.UI.Pages.Sorting;
using SilverModManager.App.UI.Resources;
using SilverModManager.App.UI.Windows;
using SilverModManager.Games.FromSoft.EldenRing;
using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// For a me3 game two mods can ship the same file from different packages, which the sync tree sees as two different
/// paths. The File Conflicts page counts those as conflicts all the same, and says that its order is a load order.
/// </summary>
public class FileConflictsLoadOrderTests(IServiceProvider provider) : AUiTest(provider)
{
    private const string Shared = "parts/wp_a_0556.partsbnd.dcx";
    private const string Merged = "msg/engus/item_dlc02.msgbnd.dcx";

    // The view model reads keyed services (image pipelines), which only a keyed provider serves.
    private sealed class FakedServices(IServiceProvider inner, IMergeIndexSource source) : IKeyedServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IMergeIndexSource) ? source : inner.GetService(serviceType);
        public object? GetKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetKeyedService(serviceType, serviceKey);
        public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ((IKeyedServiceProvider)inner).GetRequiredKeyedService(serviceType, serviceKey);
    }

    private sealed record Mods(Loadout.ReadOnly Loadout, LoadoutItemGroupId Glow, LoadoutItemGroupId Moveset, LoadoutItemGroupPriorityId GlowPriority, LoadoutItemGroupPriorityId MovesetPriority);

    /// <summary>Weapon Glow (priority 1) and Moveset (priority 2), each in a package of its own.</summary>
    private async Task<Mods> Seed(string name)
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, name);
        using var tx = Connection.BeginTransaction();
        var glow = NewGroup(tx, loadout.LoadoutId, "Weapon Glow");
        var moveset = NewGroup(tx, loadout.LoadoutId, "Moveset");
        var glowPriority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = glow, Priority = ConflictPriority.From(1) };
        var movesetPriority = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = moveset, Priority = ConflictPriority.From(2) };
        var result = await tx.Commit();
        return new Mods(
            loadout,
            LoadoutItemGroupId.From(result[glow.Value].Value),
            LoadoutItemGroupId.From(result[moveset.Value].Value),
            LoadoutItemGroupPriorityId.From(result[glowPriority.Id].Value),
            LoadoutItemGroupPriorityId.From(result[movesetPriority.Id].Value));
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

    private static ProviderFile File(string package, string relPath, ulong priority, string mod, LoadoutItemGroupId group, ulong content)
        => new(package, relPath, Hash.From(content), priority, mod, LoadoutItemId.From(group.Value));

    private static MergePathIndex IndexOf(Mods mods, params string[] paths) => MergePathIndex.Build(
        paths.SelectMany((path, i) => new[]
        {
            File("weapon_glow", path, 1, "Weapon Glow", mods.Glow, (ulong)i * 2 + 1),
            File("moveset", path, 2, "Moveset", mods.Moveset, (ulong)i * 2 + 2),
        }),
        EldenRingGame.Definition.Formats,
        new HashSet<string>());

    private static IMergeIndexSource Me3(MergePathIndex index)
    {
        var source = Substitute.For<IMergeIndexSource>();
        source.ProvidesIndex(Arg.Any<Loadout.ReadOnly>()).Returns(true);
        source.IndexOf(Arg.Any<Loadout.ReadOnly>()).Returns(index);
        return source;
    }

    private static IMergeIndexSource NotMe3()
    {
        var source = Substitute.For<IMergeIndexSource>();
        source.ProvidesIndex(Arg.Any<Loadout.ReadOnly>()).Returns(false);
        return source;
    }

    private FileConflictsViewModel Page(Mods mods, IMergeIndexSource source)
        => new(new FakedServices(Provider, source), Substitute.For<IWindowManager>(), mods.Loadout.LoadoutId);

    // The grid's rows only exist once a view realizes them; the source's items are the roots in the order they show.
    private static IEnumerable<CompositeItemModel<EntityId>> Items(FileConflictsViewModel page)
        => page.TreeDataGridAdapter.Source.Value is HierarchicalTreeDataGridSource<CompositeItemModel<EntityId>> source ? source.Items : [];

    private static (long Winning, long Losing, bool CanView) RowOf(FileConflictsViewModel page, string name)
    {
        var row = Items(page).Where(m => m.Get<NameComponent>(SharedColumns.Name.NameComponentKey).Value.Value == name).Single();
        var counts = row.Get<FileConflictsComponents.NumConflicts>(FileConflictsColumns.ConflictsColumn.NumConflictsComponentKey);
        var view = row.Get<FileConflictsComponents.ViewAction>(FileConflictsColumns.Actions.ViewComponentKey);
        return (counts.NumWinners.Value.Value, counts.NumLosers.Value.Value, view.HasConflicts.Value);
    }

    [Fact]
    public async Task APathTwoPackagesShareCountsAsAWinForTheLaterAndALossForTheEarlier()
    {
        var mods = await Seed("Shared Counts");
        var page = Page(mods, Me3(IndexOf(mods, Shared)));
        using var _ = page.Activator.Activate();

        await EventuallyOnUi(() =>
        {
            RowOf(page, "Weapon Glow").Should().Be((0, 1, true));
            RowOf(page, "Moveset").Should().Be((1, 0, true));
        });
    }

    [Fact]
    public async Task TheCountsFollowALoadoutChangeThatLeavesTheOrderAlone()
    {
        var mods = await Seed("Shared Counts Refresh");
        var index = IndexOf(mods);
        var source = Substitute.For<IMergeIndexSource>();
        source.ProvidesIndex(Arg.Any<Loadout.ReadOnly>()).Returns(true);
        source.IndexOf(Arg.Any<Loadout.ReadOnly>()).Returns(_ => index);
        var page = Page(mods, source);
        using var _ = page.Activator.Activate();

        await EventuallyOnUi(() => RowOf(page, "Weapon Glow").Should().Be((0, 0, false)));

        // Two packages starting to share a path changes no row's place or synchronizer counts; a rename and back
        // stands in for the install that did it, since the substituted index answers for the files.
        index = IndexOf(mods, Shared);
        foreach (var name in new[] { "Weapon Glow 2", "Weapon Glow" })
        {
            using var tx = Connection.BeginTransaction();
            tx.Add(mods.Glow.Value, LoadoutItem.Name, name);
            await tx.Commit();
        }

        await EventuallyOnUi(() =>
        {
            RowOf(page, "Weapon Glow").Should().Be((0, 1, true));
            RowOf(page, "Moveset").Should().Be((1, 0, true));
        });
    }

    [Fact]
    public async Task TheSharedFileCountsAreBuiltOffTheUiThread()
    {
        var mods = await Seed("Shared Counts Thread");
        var index = IndexOf(mods, Shared);
        var onUiThread = new ConcurrentQueue<bool>();
        var source = Substitute.For<IMergeIndexSource>();
        source.ProvidesIndex(Arg.Any<Loadout.ReadOnly>()).Returns(_ =>
        {
            onUiThread.Enqueue(Dispatcher.UIThread.CheckAccess());
            return true;
        });
        source.IndexOf(Arg.Any<Loadout.ReadOnly>()).Returns(_ =>
        {
            onUiThread.Enqueue(Dispatcher.UIThread.CheckAccess());
            return index;
        });
        var page = Page(mods, source);
        using var _ = page.Activator.Activate();

        await EventuallyOnUi(() => RowOf(page, "Moveset").Should().Be((1, 0, true)));

        onUiThread.Should().NotBeEmpty().And.AllSatisfy(onUi => onUi.Should().BeFalse("the index walks every item of the loadout"));
    }

    [Fact]
    public async Task APathTheAppMergesIsNotCounted()
    {
        var mods = await Seed("Merged Not Counted");
        var page = Page(mods, Me3(IndexOf(mods, Shared, Merged)));
        using var _ = page.Activator.Activate();

        await EventuallyOnUi(() =>
        {
            RowOf(page, "Weapon Glow").Should().Be((0, 1, true));
            RowOf(page, "Moveset").Should().Be((1, 0, true));
        });
    }

    [Fact]
    public async Task ALoadoutOfAGameWithoutMe3PackagesShowsNoSharedFileCounts()
    {
        var mods = await Seed("Not Me3");
        var page = Page(mods, NotMe3());
        using var _ = page.Activator.Activate();

        await EventuallyOnUi(() =>
        {
            RowOf(page, "Weapon Glow").Should().Be((0, 0, false));
            RowOf(page, "Moveset").Should().Be((0, 0, false));
        });
    }

    [Fact]
    public async Task TheListOfConflictsNamesTheLaterPackageAsTheWinner()
    {
        var mods = await Seed("Shared List");
        var index = IndexOf(mods, Shared, Merged);

        var glow = FileConflictPaths.Load(Connection, mods.Loadout.LoadoutId, mods.GlowPriority, index).Should().ContainSingle().Subject;
        glow.Path.Should().Be(new GamePath(LocationId.Me3Mods, "weapon_glow/" + Shared));
        glow.WinnerName.Should().Be("Moveset");
        glow.ThisWins.Should().BeFalse();
        glow.Losers.Should().Equal(string.Format(Language.FileConflicts_Paths_This, "Weapon Glow"));
        glow.PinsApply.Should().BeFalse("me3 picks between packages by priority, so a pin cannot change it");

        var moveset = FileConflictPaths.Load(Connection, mods.Loadout.LoadoutId, mods.MovesetPriority, index).Should().ContainSingle().Subject;
        moveset.Path.Should().Be(new GamePath(LocationId.Me3Mods, "moveset/" + Shared));
        moveset.WinnerName.Should().Be("Moveset");
        moveset.ThisWins.Should().BeTrue();
        moveset.Losers.Should().Equal("Weapon Glow");
    }

    [Fact]
    public async Task TheOrderLineSaysLoadOrderForMe3AndFlipsWithTheSortDirection()
    {
        var mods = await Seed("Order Line Me3");
        var page = Page(mods, Me3(IndexOf(mods)));

        page.OrderHint.Value.Should().Be(Language.FileConflicts_Order_LoadAscending);
        page.OrderHint.Value.Should().ContainEquivalentOf("load order").And.Contain("lower");

        page.SwitchSortDirectionCommand.Execute(R3.Unit.Default);

        page.SortDirectionCurrent.Value.Should().Be(ListSortDirection.Descending);
        page.OrderHint.Value.Should().Be(Language.FileConflicts_Order_LoadDescending);
        page.OrderHint.Value.Should().Contain("higher");
    }

    [Fact]
    public async Task TheOrderLineForOtherGamesTalksOfPriorityAndFlipsToo()
    {
        var mods = await Seed("Order Line Other");
        var page = Page(mods, NotMe3());

        page.OrderHint.Value.Should().Be(Language.FileConflicts_Order_PriorityAscending);
        page.OrderHint.Value.Should().NotContainEquivalentOf("load order");

        page.SwitchSortDirectionCommand.Execute(R3.Unit.Default);

        page.OrderHint.Value.Should().Be(Language.FileConflicts_Order_PriorityDescending);
    }
}
