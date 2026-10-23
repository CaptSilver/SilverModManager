using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The sync tree leaves out winners that are deletes, so <see cref="ALoadoutSynchronizer.DeletedWinners"/> is
/// the only way to learn which paths the loadout says are gone.
/// </summary>
public class DeletedWinnersTests(ITestOutputHelper helper) : AStubbedGameTest<DeletedWinnersTests>(helper)
{
    private static readonly GamePath DeletedPath = new(LocationId.Game, "Data/gone.txt");

    private ALoadoutSynchronizer Queries => (ALoadoutSynchronizer)Synchronizer;

    // Same shape the copy backend writes when the game deletes a file it was serving.
    private static void AddDelete(ITransaction tx, LoadoutId loadoutId, LoadoutItemGroupId group, GamePath path)
    {
        _ = new DeletedFile.New(tx, out var id)
        {
            Reason = "Reified delete",
            LoadoutItemWithTargetPath = new LoadoutItemWithTargetPath.New(tx, id)
            {
                TargetPath = path.ToGamePathParentTuple(loadoutId),
                LoadoutItem = new LoadoutItem.New(tx, id)
                {
                    LoadoutId = loadoutId,
                    Name = path.Path,
                    ParentId = group,
                },
            },
        };
    }

    [Fact]
    public async Task ADeletedFileInAnEnabledGroupIsListed()
    {
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Deleter");
        AddDelete(tx, loadout.LoadoutId, group, DeletedPath);
        await tx.Commit();

        Queries.DeletedWinners(loadout.Rebase()).Should().Equal(DeletedPath);
    }

    [Fact]
    public async Task ADeletedFileInADisabledGroupIsNotListed()
    {
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Deleter");
        AddDelete(tx, loadout.LoadoutId, group, DeletedPath);
        var result = await tx.Commit();
        await DisableItem(result[group.Value]);

        Queries.DeletedWinners(loadout.Rebase()).Should().BeEmpty();
    }

    [Fact]
    public async Task AHigherPriorityFileAtTheSamePathHidesTheDelete()
    {
        var loadout = await CreateLoadout();

        using var tx = Connection.BeginTransaction();
        var deleter = AddEmptyGroup(tx, loadout.LoadoutId, "Deleter");
        AddDelete(tx, loadout.LoadoutId, deleter, DeletedPath);
        var provider = AddEmptyGroup(tx, loadout.LoadoutId, "Provider");
        AddFile(tx, loadout.LoadoutId, provider, DeletedPath, "bytes");
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = deleter, Priority = ConflictPriority.From(1) };
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadout.LoadoutId, TargetId = provider, Priority = ConflictPriority.From(2) };
        await tx.Commit();

        Queries.DeletedWinners(loadout.Rebase()).Should().BeEmpty();
    }
}
