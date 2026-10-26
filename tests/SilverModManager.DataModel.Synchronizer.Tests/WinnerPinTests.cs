using FluentAssertions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A pin on a path overrides group priority in the winner queries for that path only; everything else keeps
/// following priority.
/// </summary>
public class WinnerPinTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<WinnerPinTests>(helper)
{
    private static readonly GamePath Conflict = new(LocationId.Game, "archive/pc/mod/conflict.archive");
    private static readonly GamePath Sole = new(LocationId.Game, "archive/pc/mod/sole.archive");
    private static readonly GamePath Unpinned = new(LocationId.Game, "archive/pc/mod/unpinned.archive");

    private record Mods(LoadoutItemGroupId Low, LoadoutItemGroupId High, Hash LowHash, Hash HighHash);

    /// <summary>Low (priority 1) and High (priority 2) both provide Conflict and Unpinned; Low alone provides Sole.</summary>
    private async Task<Mods> AddLowAndHigh(LoadoutId loadoutId)
    {
        using var tx = Connection.BeginTransaction();
        var low = AddEmptyGroup(tx, loadoutId, "Low");
        var high = AddEmptyGroup(tx, loadoutId, "High");
        AddFile(tx, loadoutId, low, Conflict, "conflict from low", out var lowHash, out _);
        AddFile(tx, loadoutId, high, Conflict, "conflict from high", out var highHash, out _);
        AddFile(tx, loadoutId, low, Unpinned, "unpinned from low");
        AddFile(tx, loadoutId, high, Unpinned, "unpinned from high");
        AddFile(tx, loadoutId, low, Sole, "sole from low");
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = low, Priority = ConflictPriority.From(1) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = high, Priority = ConflictPriority.From(2) };
        var result = await tx.Commit();
        return new Mods(LoadoutItemGroupId.From(result[low].Value), LoadoutItemGroupId.From(result[high].Value), lowHash, highHash);
    }

    private async Task<Hash> WinnerHash(Loadout.ReadOnly loadout, GamePath path)
    {
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        return tree[path].Loadout.Hash;
    }

    [Fact]
    public async Task WithoutAPinTheHigherPriorityGroupWins()
    {
        var loadout = await CreateLoadout();
        var mods = await AddLowAndHigh(loadout.LoadoutId);

        (await WinnerHash(loadout, Conflict)).Should().Be(mods.HighHash);
    }

    [Fact]
    public async Task APinOnTheLowerPriorityGroupMakesItWinThatPathOnly()
    {
        var loadout = await CreateLoadout();
        var mods = await AddLowAndHigh(loadout.LoadoutId);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, mods.Low);

        (await WinnerHash(loadout, Conflict)).Should().Be(mods.LowHash);
        (await WinnerHash(loadout, Unpinned)).Should().Be("unpinned from high".xxHash3AsUtf8(), "other paths still follow priority");
    }

    [Fact]
    public async Task APinOnAPathWithOneProviderChangesNothing()
    {
        var loadout = await CreateLoadout();
        var mods = await AddLowAndHigh(loadout.LoadoutId);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Sole, mods.High);

        (await WinnerHash(loadout, Sole)).Should().Be("sole from low".xxHash3AsUtf8(), "the pinned group has no file at the path");
    }

    [Fact]
    public async Task APinOnADisabledGroupFallsBackToPriority()
    {
        var loadout = await CreateLoadout();
        var mods = await AddLowAndHigh(loadout.LoadoutId);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, mods.Low);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(mods.Low.Value, LoadoutItem.Disabled, SilverModManager.MnemonicDB.Abstractions.ElementComparers.Null.Instance);
            await tx.Commit();
        }

        (await WinnerHash(loadout, Conflict)).Should().Be(mods.HighHash);
    }

    [Fact]
    public async Task APinWhoseGroupWasRemovedFallsBackToPriority()
    {
        var loadout = await CreateLoadout();
        var mods = await AddLowAndHigh(loadout.LoadoutId);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, mods.Low);

        await LoadoutManager.RemoveItems([mods.Low]);

        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
        (await WinnerHash(loadout, Conflict)).Should().Be(mods.HighHash);
    }

    [Fact]
    public async Task APinInOneLoadoutDoesNotAffectAnother()
    {
        var first = await CreateLoadout();
        var second = await CreateLoadout();
        var firstMods = await AddLowAndHigh(first.LoadoutId);
        var secondMods = await AddLowAndHigh(second.LoadoutId);
        await LoadoutManager.PinFileWinner(first.LoadoutId, Conflict, firstMods.Low);
        await LoadoutManager.PinFileWinner(second.LoadoutId, Conflict, secondMods.High);

        (await WinnerHash(first, Conflict)).Should().Be(firstMods.LowHash);
        (await WinnerHash(second, Conflict)).Should().Be(secondMods.HighHash);
    }

    [Fact]
    public async Task APinnedPathStaysAConflictAndTheLoserIsTheUnpinnedGroup()
    {
        var loadout = await CreateLoadout();
        var mods = await AddLowAndHigh(loadout.LoadoutId);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, mods.Low);

        var rows = Connection.Query<(EntityId Target, long Winning, long Losing)>(
            $"SELECT Target, len(WinningFiles), len(LosingFiles) FROM synchronizer.PriorityGroups({Connection.Db})"
        ).ToDictionary(r => r.Target);

        rows[mods.Low.Value].Winning.Should().Be(1, "Low's pinned file wins");
        rows[mods.High.Value].Losing.Should().Be(1, "High loses the pinned path");
    }
}
