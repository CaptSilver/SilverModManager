using FluentAssertions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Games.TestFramework;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Pinning which group provides one path of a loadout: one row per path, re-pinning replaces it, clearing
/// retracts it, and a group from another loadout is refused.
/// </summary>
public class FileWinnerPinTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<FileWinnerPinTests>(helper)
{
    private static readonly GamePath Conflict = new(LocationId.Game, "archive/pc/mod/conflict.archive");
    private static readonly GamePath Other = new(LocationId.Game, "archive/pc/mod/other.archive");

    private async Task<LoadoutItemGroupId> AddGroup(LoadoutId loadoutId, string name)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, name);
        AddFile(tx, loadoutId, group, Conflict, name);
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    [Fact]
    public async Task PinningWritesOneRowKeyedByTheLoadoutAndPath()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout.LoadoutId, "A");

        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, group);

        var pin = LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        pin.WinnerGroupId.Value.Should().Be(group.Value);
        pin.TargetPath.Item1.Should().Be(loadout.Id);
        pin.TargetPath.Item2.Should().Be(Conflict.LocationId);
        pin.TargetPath.Item3.Should().Be(Conflict.Path);
    }

    [Fact]
    public async Task PinningAgainReplacesTheGroupInsteadOfAddingARow()
    {
        var loadout = await CreateLoadout();
        var a = await AddGroup(loadout.LoadoutId, "A");
        var b = await AddGroup(loadout.LoadoutId, "B");
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, a);

        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, b);

        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle()
            .Which.WinnerGroupId.Value.Should().Be(b.Value);
    }

    [Fact]
    public async Task PinsOnDifferentPathsAreIndependent()
    {
        var loadout = await CreateLoadout();
        var a = await AddGroup(loadout.LoadoutId, "A");
        var b = await AddGroup(loadout.LoadoutId, "B");
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, a);
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Other, b);

        await LoadoutManager.ClearFileWinner(loadout.LoadoutId, Conflict);

        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle()
            .Which.TargetPath.Item3.Should().Be(Other.Path);
    }

    [Fact]
    public async Task ClearingRetractsThePin()
    {
        var loadout = await CreateLoadout();
        var group = await AddGroup(loadout.LoadoutId, "A");
        await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, group);

        await LoadoutManager.ClearFileWinner(loadout.LoadoutId, Conflict);

        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task ClearingAPathWithNoPinWritesNothing()
    {
        var loadout = await CreateLoadout();
        var basis = Connection.Db.BasisTxId;

        await LoadoutManager.ClearFileWinner(loadout.LoadoutId, Conflict);

        Connection.Db.BasisTxId.Value.Should().Be(basis.Value);
    }

    [Fact]
    public async Task AGroupOutsideTheLoadoutIsRefusedAndNothingIsWritten()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        var foreign = await AddGroup(other.LoadoutId, "Foreign");

        var act = async () => await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, foreign);

        await act.Should().ThrowAsync<ArgumentException>();
        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task AFileIdInPlaceOfAGroupIsRefused()
    {
        var loadout = await CreateLoadout();
        LoadoutItemGroupId fileId;
        using (var tx = Connection.BeginTransaction())
        {
            var group = AddEmptyGroup(tx, loadout.LoadoutId, "A");
            var file = AddFile(tx, loadout.LoadoutId, group, Conflict, "A");
            var result = await tx.Commit();
            fileId = LoadoutItemGroupId.From(result[file].Value);
        }

        var act = async () => await LoadoutManager.PinFileWinner(loadout.LoadoutId, Conflict, fileId);

        await act.Should().ThrowAsync<ArgumentException>();
        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }
}
