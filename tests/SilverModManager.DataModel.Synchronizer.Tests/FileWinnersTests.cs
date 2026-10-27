using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// <see cref="FileWinners"/> restates the SQL macro that picks the deployed file of a path. These tests run both on one
/// fixture so the two cannot drift apart unnoticed.
/// </summary>
public class FileWinnersTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<FileWinnersTests>(helper)
{
    private static GamePath P(string name) => new(LocationId.Game, $"archive/pc/mod/{name}.archive");

    private async Task<Loadout.ReadOnly> Fixture()
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId low, high, other;
        using (var tx = Connection.BeginTransaction())
        {
            var l = AddEmptyGroup(tx, loadout.LoadoutId, "Low");
            var h = AddEmptyGroup(tx, loadout.LoadoutId, "High");
            var o = AddEmptyGroup(tx, loadout.LoadoutId, "Other");
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = l, Priority = ConflictPriority.From(1) };
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = h, Priority = ConflictPriority.From(3) };
            _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = o, Priority = ConflictPriority.From(2) };
            var result = await tx.Commit();
            (low, high, other) = (result[l], result[h], result[o]);
        }

        LoadoutItemGroupId inner;
        using (var tx = Connection.BeginTransaction())
        {
            // A group of High's with no priority row of its own: it ranks zero, whatever High has.
            var group = new LoadoutItemGroup.New(tx, out var id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { LoadoutId = loadout.LoadoutId, Name = "High inner", ParentId = high },
            };
            inner = (await tx.Commit())[group.Id];
        }

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in new[] { low, high, other })
            {
                AddFile(tx, loadout.LoadoutId, group, P("plain"), $"plain {group}");
                AddFile(tx, loadout.LoadoutId, group, P("pinned-low"), $"pinned-low {group}");
                AddFile(tx, loadout.LoadoutId, group, P("pinned-high"), $"pinned-high {group}");
            }
            AddFile(tx, loadout.LoadoutId, inner, P("nested"), "nested inner");
            AddFile(tx, loadout.LoadoutId, low, P("nested"), "nested low");
            await tx.Commit();
        }

        await LoadoutManager.PinFileWinner(loadout.LoadoutId, P("pinned-low"), low);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, P("pinned-high"), high);
        return Refresh(loadout);
    }

    [Fact]
    public async Task ItPicksTheSameGroupAsTheSynchronizersWinnerQueryForEveryPath()
    {
        var loadout = await Fixture();
        var db = Connection.Db;

        var sql = Connection.Query<(EntityId Id, EntityId Parent)>($"SELECT Id, Parent FROM synchronizer.WinningLeafLoadoutItem({db}) WHERE Loadout = {loadout.LoadoutId}")
            .ToDictionary(r => (GamePath)LoadoutItemWithTargetPath.Load(db, r.Id).TargetPath, r => r.Parent);

        var winners = FileWinners.Of(db, loadout.LoadoutId);
        var mine = LoadoutItem.FindByLoadout(db, loadout).OfTypeLoadoutItemWithTargetPath()
            .Select(i => (Path: (GamePath)i.TargetPath, Parent: LoadoutItemGroupId.From(i.AsLoadoutItem().ParentId.Value)))
            .GroupBy(x => x.Path)
            .ToDictionary(g => g.Key, g => g.MaxBy(x => winners.RankOf(x.Path, x.Parent)).Parent.Value);

        sql.Should().HaveCount(4);
        mine.Should().BeEquivalentTo(sql);
    }
}
