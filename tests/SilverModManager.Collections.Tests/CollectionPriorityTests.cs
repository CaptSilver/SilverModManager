using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.Collections.Tests;

/// <summary>
/// Collections take part in the conflict order as units: the collection that is higher up wins the files its mods
/// share with another collection, whether it was dragged there or a rule put it there.
/// </summary>
public class CollectionPriorityTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<CollectionPriorityTests>(helper)
{
    private static readonly GamePath Shared = new(LocationId.Game, "archive/pc/mod/shared.archive");
    private static readonly GamePath OnlyInFirst = new(LocationId.Game, "archive/pc/mod/first_only.archive");

    private record Collections(
        LoadoutItemGroupId First,
        LoadoutItemGroupId Second,
        LoadoutItemGroupPriorityId[] FirstRows,
        LoadoutItemGroupPriorityId[] SecondRows);

    /// <summary>
    /// Two collections of two mods each, in priority order: first (a, b), second (c, d). Mods b and c both provide
    /// <see cref="Shared"/>, and a provides a file nobody else has.
    /// </summary>
    private async Task<Collections> AddTwoCollections(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var first = NewCollection(tx, loadoutId, "First pack");
        var second = NewCollection(tx, loadoutId, "Second pack");
        var a = AddEmptyGroup(tx, loadoutId, "First a", first);
        var b = AddEmptyGroup(tx, loadoutId, "First b", first);
        var c = AddEmptyGroup(tx, loadoutId, "Second c", second);
        var d = AddEmptyGroup(tx, loadoutId, "Second d", second);
        AddFile(tx, loadoutId, a, OnlyInFirst, "first only");
        AddFile(tx, loadoutId, b, Shared, "shared from first");
        AddFile(tx, loadoutId, c, Shared, "shared from second");
        var rows = new[] { a, b, c, d }
            .Select((group, i) => new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From((ulong)i + 1) })
            .ToArray();
        var result = await tx.Commit();

        LoadoutItemGroupPriorityId Row(int i) => LoadoutItemGroupPriorityId.From(result[rows[i].Id].Value);
        return new Collections(
            LoadoutItemGroupId.From(result[first].Value),
            LoadoutItemGroupId.From(result[second].Value),
            [Row(0), Row(1)],
            [Row(2), Row(3)]);
    }

    private static LoadoutItemGroupId NewCollection(ITransaction tx, LoadoutId loadoutId, string name)
    {
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = true,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadoutId, Name = name },
            },
        };
        return LoadoutItemGroupId.From(id.Value);
    }

    private async Task<NexusMods.Hashing.xxHash3.Hash> WinnerOfShared(Loadout.ReadOnly loadout)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        return tree[Shared].Loadout.Hash;
    }

    [Fact]
    public async Task TheCollectionHigherInTheOrderWinsTheFilesItSharesWithAnother()
    {
        var loadout = await CreateLoadout();
        await AddTwoCollections(loadout.LoadoutId);

        (await WinnerOfShared(loadout)).Should().Be("shared from second".xxHash3AsUtf8());
    }

    [Fact]
    public async Task MovingACollectionAboveAnotherMakesItsFilesWin()
    {
        var loadout = await CreateLoadout();
        var collections = await AddTwoCollections(loadout.LoadoutId);

        // The whole first collection goes over the second collection's top mod.
        var result = await LoadoutManager.ResolveFileConflicts(collections.FirstRows, collections.SecondRows[^1]);

        result.IsRuleViolation.Should().BeFalse();
        (await WinnerOfShared(loadout)).Should().Be("shared from first".xxHash3AsUtf8());
        var order = LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .OrderBy(p => p.Priority).Select(p => p.Target.AsLoadoutItem().Name).ToArray();
        order.Should().Equal("Second c", "Second d", "First a", "First b");
    }

    [Fact]
    public async Task ARuleNamingACollectionPutsItsFilesAboveTheOthers()
    {
        var loadout = await CreateLoadout();
        var collections = await AddTwoCollections(loadout.LoadoutId);

        var result = await LoadoutManager.AddGroupRule(loadout.LoadoutId, collections.First, collections.Second, GroupRuleKind.After);

        result.IsCycle.Should().BeFalse();
        (await WinnerOfShared(loadout)).Should().Be("shared from first".xxHash3AsUtf8());
    }

    [Fact]
    public async Task ADragThatBreaksACollectionRuleIsRefusedAndTheWinnerStays()
    {
        var loadout = await CreateLoadout();
        var collections = await AddTwoCollections(loadout.LoadoutId);
        await LoadoutManager.AddGroupRule(loadout.LoadoutId, collections.First, collections.Second, GroupRuleKind.After);

        // Sends the first collection to the bottom, under the second one it has to stay above.
        var result = await LoadoutManager.LoseAllFileConflicts(collections.FirstRows);

        result.IsRuleViolation.Should().BeTrue();
        result.Rule.SourceId.Should().Be(collections.First);
        (await WinnerOfShared(loadout)).Should().Be("shared from first".xxHash3AsUtf8());
    }
}
