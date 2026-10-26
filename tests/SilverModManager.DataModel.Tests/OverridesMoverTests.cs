using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.DataModel.Overrides;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Moving the files in a loadout's Overrides group into a mod: one transaction, what a duplicate does, what the
/// preview says the moved file would lose to, and a game's veto.
/// </summary>
public class OverridesMoverTests(ITestOutputHelper helper) : ACyberpunkIsolatedGameTest<OverridesMoverTests>(helper)
{
    private static readonly GamePath PathA = new(LocationId.Game, "archive/pc/mod/a.archive");
    private static readonly GamePath PathB = new(LocationId.Game, "archive/pc/mod/b.archive");

    private IOverridesMover Mover => ServiceProvider.GetRequiredService<IOverridesMover>();

    [Fact]
    public async Task AnEmptyGroupFromTheMoverHasOnePriorityRowAboveTheExistingOnes()
    {
        var loadout = await CreateLoadout();
        var existing = await AddMod(loadout.LoadoutId, "Existing", priority: 7UL, files: [(PathA, "existing")]);

        var created = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Fixes");

        var db = Connection.Db;
        var group = LoadoutItemGroup.Load(db, created);
        group.IsGroup.Should().BeTrue();
        group.AsLoadoutItem().Name.Should().Be("Fixes");
        group.AsLoadoutItem().LoadoutId.Should().Be(loadout.LoadoutId);
        var row = LoadoutItemGroupPriority.FindByTarget(db, created).Should().ContainSingle().Subject;
        row.Priority.Value.Should().Be(8UL, "one above the highest existing row");
        LoadoutItemGroupPriority.FindByTarget(db, existing).Should().ContainSingle();
    }

    [Fact]
    public async Task MovingReparentsTheRowsInOneTransactionAndKeepsTheirContent()
    {
        var loadout = await CreateLoadout();
        var overrides = await AddOverrides(loadout, (PathA, "aaa"), (PathB, "bbb"));
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");
        var rowIds = overrides.Select(r => r.Id).ToArray();
        var basis = Connection.Db.BasisTxId;

        var result = await Mover.Move(loadout.LoadoutId, [PathA, PathB], target, new MoveOptions());

        result.Moved.Should().BeEquivalentTo([PathA, PathB]);
        result.Skipped.Should().BeEmpty();
        Connection.Db.BasisTxId.Value.Should().Be(basis.Value + 1, "the whole move is one transaction");

        var children = LoadoutItemGroup.Load(Connection.Db, target).Children.OfTypeLoadoutItemWithTargetPath().ToArray();
        children.Select(c => c.Id).Should().BeEquivalentTo(rowIds, "the rows are re-parented, not copied");
        children.Select(c => (GamePath)c.TargetPath).Should().BeEquivalentTo([PathA, PathB]);
        HashOf(children.Single(c => c.TargetPath == PathA)).Should().Be("aaa".xxHash3AsUtf8());
        OverridesRows(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task ADuplicateInTheTargetIsReplacedByDefault()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "from overrides"));
        var target = await AddMod(loadout.LoadoutId, "Mod", priority: 3UL, files: [(PathA, "old"), (PathB, "keep")]);

        var result = await Mover.Move(loadout.LoadoutId, [PathA], target, new MoveOptions());

        result.Moved.Should().ContainSingle().Which.Should().Be(PathA);
        var rows = LoadoutItemGroup.Load(Connection.Db, target).Children.OfTypeLoadoutItemWithTargetPath().ToArray();
        rows.Should().HaveCount(2);
        HashOf(rows.Single(r => r.TargetPath == PathA)).Should().Be("from overrides".xxHash3AsUtf8());
        HashOf(rows.Single(r => r.TargetPath == PathB)).Should().Be("keep".xxHash3AsUtf8());
    }

    [Fact]
    public async Task ADuplicateIsLeftInOverridesWhenReplacingIsOff()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "from overrides"), (PathB, "b"));
        var target = await AddMod(loadout.LoadoutId, "Mod", priority: 3UL, files: [(PathA, "old")]);

        var result = await Mover.Move(loadout.LoadoutId, [PathA, PathB], target, new MoveOptions { ReplaceExisting = false });

        result.Moved.Should().ContainSingle().Which.Should().Be(PathB);
        result.Skipped.Should().ContainSingle().Which.Should().Be(PathA);
        ((GamePath)OverridesRows(loadout).Should().ContainSingle().Subject.TargetPath).Should().Be(PathA);
        HashOf(LoadoutItemGroup.Load(Connection.Db, target).Children.OfTypeLoadoutItemWithTargetPath()
            .Single(r => r.TargetPath == PathA)).Should().Be("old".xxHash3AsUtf8());
    }

    [Fact]
    public async Task ARecordedDeleteBecomesTheTargetsDeleteMarker()
    {
        var loadout = await CreateLoadout();
        await AddOverridesDeletes(loadout, PathA);
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");

        await Mover.Move(loadout.LoadoutId, [PathA], target, new MoveOptions());

        var row = LoadoutItemGroup.Load(Connection.Db, target).Children.OfTypeLoadoutItemWithTargetPath().Should().ContainSingle().Subject;
        ((GamePath)row.TargetPath).Should().Be(PathA);
        row.TryGetAsDeletedFile(out _).Should().BeTrue();
        OverridesRows(loadout).Should().BeEmpty();
    }

    [Fact]
    public async Task PinningIsOffByDefaultAndPinsTheMovedPathsWhenAsked()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "a"), (PathB, "b"));
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");

        await Mover.Move(loadout.LoadoutId, [PathA], target, new MoveOptions());
        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();

        await Mover.Move(loadout.LoadoutId, [PathB], target, new MoveOptions { PinMovedPaths = true });
        var pin = LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle().Subject;
        pin.WinnerGroupId.Should().Be(target);
        pin.TargetPath.Should().Be((loadout.Id, PathB.LocationId, PathB.Path));
    }

    [Fact]
    public async Task PinningAPathThatAlreadyHasAPinRetargetsThePin()
    {
        var loadout = await CreateLoadout();
        var older = await AddMod(loadout.LoadoutId, "Older", priority: 1UL, files: [(PathA, "older")]);
        using (var tx = Connection.BeginTransaction())
        {
            _ = new LoadoutFileWinner.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                TargetPath = PathA.ToGamePathParentTuple(loadout.Id),
                WinnerGroupId = older,
            };
            await tx.Commit();
        }
        await AddOverrides(loadout, (PathA, "a"));
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");

        await Mover.Move(loadout.LoadoutId, [PathA], target, new MoveOptions { PinMovedPaths = true });

        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().ContainSingle()
            .Which.WinnerGroupId.Should().Be(target, "one pin per path, now naming the group that got the file");
    }

    [Fact]
    public async Task ASkippedDuplicateIsNotPinned()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "a"));
        var target = await AddMod(loadout.LoadoutId, "Mod", priority: 3UL, files: [(PathA, "old")]);

        await Mover.Move(loadout.LoadoutId, [PathA], target, new MoveOptions { ReplaceExisting = false, PinMovedPaths = true });

        LoadoutFileWinner.FindByLoadout(Connection.Db, loadout.LoadoutId).Should().BeEmpty();
    }

    [Fact]
    public async Task TheOverridesGroupIsNotADestination()
    {
        var loadout = await CreateLoadout();
        var overrides = await AddOverrides(loadout, (PathA, "a"));
        var overridesGroup = LoadoutItemGroupId.From(overrides[0].AsLoadoutItem().ParentId.Value);

        var act = () => Mover.Move(loadout.LoadoutId, [PathA], overridesGroup, new MoveOptions());

        await act.Should().ThrowAsync<OverridesMoveRefusedException>();
        OverridesRows(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task AGroupOfAnotherLoadoutIsNotADestination()
    {
        var loadout = await CreateLoadout();
        var other = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "a"));
        var foreign = await Mover.CreateEmptyGroup(other.LoadoutId, "Other");

        var act = () => Mover.Move(loadout.LoadoutId, [PathA], foreign, new MoveOptions());

        await act.Should().ThrowAsync<OverridesMoveRefusedException>();
        OverridesRows(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task APathWithNoOverridesRowRefusesTheWholeMoveBeforeWriting()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "a"));
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");
        var basis = Connection.Db.BasisTxId;

        var act = () => Mover.Move(loadout.LoadoutId, [PathA, PathB], target, new MoveOptions());

        await act.Should().ThrowAsync<OverridesMoveRefusedException>();
        Connection.Db.BasisTxId.Should().Be(basis, "nothing was written");
        OverridesRows(loadout).Should().ContainSingle();
    }

    [Fact]
    public async Task AGameThatRefusesAPathMakesMoveThrowBeforeWriting()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "a"), (PathB, "b"));
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");
        var mover = new OverridesMover(Connection, _ => new RefusingSynchronizer(ServiceProvider, PathB));
        var basis = Connection.Db.BasisTxId;

        var act = () => mover.Move(loadout.LoadoutId, [PathA, PathB], target, new MoveOptions());

        (await act.Should().ThrowAsync<OverridesMoveRefusedException>()).Which.Message.Should().Contain(PathB.Path.ToString());
        Connection.Db.BasisTxId.Should().Be(basis, "the refusal comes before any write, even for the path the game allows");
        OverridesRows(loadout).Should().HaveCount(2);
    }

    [Fact]
    public async Task PreviewDescribesTheRowTheTargetsRowAndTheirHashes()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "from overrides"));
        var target = await AddMod(loadout.LoadoutId, "Mod", priority: 3UL, files: [(PathA, "old")]);

        var previews = Mover.Preview(loadout.LoadoutId, [PathA, PathB], target);

        var a = previews.Single(p => p.Path == PathA);
        a.InOverrides.Should().BeTrue();
        a.IsDelete.Should().BeFalse();
        a.Hash.Should().Be("from overrides".xxHash3AsUtf8());
        a.TargetHasPath.Should().BeTrue();
        a.TargetIsDelete.Should().BeFalse();
        a.TargetHash.Should().Be("old".xxHash3AsUtf8());

        var b = previews.Single(p => p.Path == PathB);
        b.InOverrides.Should().BeFalse();
        b.TargetHasPath.Should().BeFalse();
        b.WinnerAfterMove.Should().BeNull("there is nothing to move, so nothing to decide");
    }

    [Fact]
    public async Task PreviewFlagsARecordedDeleteAndATargetDeleteMarker()
    {
        var loadout = await CreateLoadout();
        await AddOverridesDeletes(loadout, PathA);
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Mod");

        var preview = Mover.Preview(loadout.LoadoutId, [PathA], target).Single();

        preview.InOverrides.Should().BeTrue();
        preview.IsDelete.Should().BeTrue();
        preview.Hash.Should().BeNull();
    }

    [Fact]
    public async Task PreviewNamesTheHigherPriorityModTheMovedFileWouldLoseTo()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "from overrides"));
        var target = await AddMod(loadout.LoadoutId, "Low", priority: 1UL, files: []);
        var higher = await AddMod(loadout.LoadoutId, "High", priority: 9UL, files: [(PathA, "high")]);

        var preview = Mover.Preview(loadout.LoadoutId, [PathA], target).Single();

        preview.WinsAfterMove.Should().BeFalse();
        preview.WinnerAfterMove.Should().Be(higher);
    }

    [Fact]
    public async Task PreviewSaysTheMovedFileWinsOverALowerPriorityMod()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "from overrides"));
        await AddMod(loadout.LoadoutId, "Low", priority: 1UL, files: [(PathA, "low")]);
        var target = await AddMod(loadout.LoadoutId, "High", priority: 9UL, files: []);

        var preview = Mover.Preview(loadout.LoadoutId, [PathA], target).Single();

        preview.WinsAfterMove.Should().BeTrue();
        preview.WinnerAfterMove.Should().Be(target);
    }

    [Fact]
    public async Task PreviewIgnoresADisabledModAndCountsAFreshGroupAsTheTopOne()
    {
        var loadout = await CreateLoadout();
        await AddOverrides(loadout, (PathA, "from overrides"));
        var disabled = await AddMod(loadout.LoadoutId, "Disabled", priority: 50UL, files: [(PathA, "x")]);
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(disabled.Value, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        var target = await Mover.CreateEmptyGroup(loadout.LoadoutId, "Fresh");

        var preview = Mover.Preview(loadout.LoadoutId, [PathA], target).Single();

        preview.WinsAfterMove.Should().BeTrue("the disabled mod provides nothing, and the new group is above every row");
    }

    private static Hash HashOf(LoadoutItemWithTargetPath.ReadOnly row)
    {
        row.TryGetAsLoadoutFile(out var file).Should().BeTrue("the row should be a file, not a delete");
        return file.Hash;
    }

    private IEnumerable<LoadoutItemWithTargetPath.ReadOnly> OverridesRows(Loadout.ReadOnly loadout) =>
        LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).SelectMany(g => g.AsLoadoutItemGroup().Children.OfTypeLoadoutItemWithTargetPath());

    private Task<LoadoutItemWithTargetPath.ReadOnly[]> AddOverrides(Loadout.ReadOnly loadout, params (GamePath Path, string Content)[] files) =>
        AddOverridesRows(loadout, files, []);

    private Task<LoadoutItemWithTargetPath.ReadOnly[]> AddOverridesDeletes(Loadout.ReadOnly loadout, params GamePath[] deleted) =>
        AddOverridesRows(loadout, [], deleted);

    private async Task<LoadoutItemWithTargetPath.ReadOnly[]> AddOverridesRows(
        Loadout.ReadOnly loadout,
        (GamePath Path, string Content)[] files,
        GamePath[] deleted)
    {
        var synchronizer = (ALoadoutSynchronizer)Synchronizer;
        using (var create = Connection.BeginTransaction())
        {
            synchronizer.GetOrCreateOverridesGroup(create, loadout);
            await create.Commit();
        }

        var group = LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single();
        var fresh = Loadout.Load(Connection.Db, loadout.Id);
        using var tx = Connection.BeginTransaction();
        foreach (var (path, content) in files)
            AddFile(tx, loadout.LoadoutId, LoadoutItemGroupId.From(group.Id), path, content);
        foreach (var path in deleted)
            OverridesGroupItems.AddReifiedDelete(tx, fresh, LoadoutOverridesGroupId.From(group.Id), path);
        await tx.Commit();
        return OverridesRows(loadout).ToArray();
    }

    private async Task<LoadoutItemGroupId> AddMod(LoadoutId loadoutId, string name, ulong priority, (GamePath Path, string Content)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, name);
        foreach (var (path, content) in files)
            AddFile(tx, loadoutId, group, path, content);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private sealed class RefusingSynchronizer(IServiceProvider provider, GamePath refused) : ALoadoutSynchronizer(provider)
    {
        public override bool CanMoveOverrides(Loadout.ReadOnly loadout, GamePath path) => path != refused;
    }
}
