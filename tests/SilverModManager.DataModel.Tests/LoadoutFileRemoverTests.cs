using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Deployment;
using SilverModManager.DataModel.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// Removing file rows from a loadout: which rows a path reaches, which of them may go, and the restore point that
/// makes the removal undoable.
/// </summary>
public class LoadoutFileRemoverTests(ITestOutputHelper helper) : ANexusCollectionTest<LoadoutFileRemoverTests>(helper)
{
    private static readonly GamePath PathA = new(LocationId.Game, "archive/pc/mod/a.archive");
    private static readonly GamePath PathB = new(LocationId.Game, "archive/pc/mod/b.archive");
    private static readonly GamePath Vanilla = new(LocationId.Game, "bin/x64/Cyberpunk2077.exe");

    private ILoadoutFileRemover Remover => ServiceProvider.GetRequiredService<ILoadoutFileRemover>();
    private ICollectionOwnership Ownership => ServiceProvider.GetRequiredService<ICollectionOwnership>();
    private UndoService Undo => ServiceProvider.GetRequiredService<UndoService>();

    [Fact]
    public async Task PlanListsEveryRowAtThePathWithItsGroupAndCollection()
    {
        var loadout = await NewLoadout();
        var one = await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "one")]);
        var two = await AddMod(loadout.LoadoutId, "Two", 2UL, [(PathA, "two")]);

        var plan = Remover.Plan(Request(loadout, PathA));

        plan.Rows.Should().HaveCount(2);
        plan.Rows.Select(r => r.GroupId).Should().BeEquivalentTo([one, two]);
        plan.Rows.Select(r => r.GroupName).Should().BeEquivalentTo(["One", "Two"]);
        plan.Rows.Should().OnlyContain(r => r.CollectionName == null && r.LockReason == null);
        plan.Rows.Count(r => r.IsWinner).Should().Be(1);
        plan.Rows.Single(r => r.IsWinner).GroupId.Should().Be(two, "the higher priority wins");
    }

    [Fact]
    public async Task PlanIncludesDisabledAndLosingRows()
    {
        var loadout = await NewLoadout();
        var disabled = await AddMod(loadout.LoadoutId, "Off", 9UL, [(PathA, "off")]);
        await AddMod(loadout.LoadoutId, "On", 1UL, [(PathA, "on")]);
        await Disable(disabled);

        var plan = Remover.Plan(Request(loadout, PathA));

        plan.Removable.Should().HaveCount(2);
        plan.Rows.Single(r => r.GroupId == disabled).IsDisabled.Should().BeTrue();
        plan.Rows.Single(r => r.GroupId == disabled).IsWinner.Should().BeFalse();
        plan.Rows.Single(r => r.GroupId != disabled).IsWinner.Should().BeTrue();
    }

    [Fact]
    public async Task AFolderExpandsToEverythingUnderItAndNotToSiblingsWithTheSamePrefix()
    {
        var loadout = await NewLoadout();
        var folder = new GamePath(LocationId.Game, "archive/pc/Foo");
        await AddMod(loadout.LoadoutId, "M", 1UL, [
            (folder / "x.archive", "x"),
            (folder / "deep" / "y.archive", "y"),
            (new GamePath(LocationId.Game, "archive/pc/FooBar/x.archive"), "z"),
        ]);

        var plan = Remover.Plan(Request(loadout, folder));

        plan.Rows.Select(r => r.Path).Should().BeEquivalentTo([folder / "x.archive", folder / "deep" / "y.archive"]);
    }

    [Fact]
    public async Task ARowInALockedCollectionIsReportedLockedWithTheOwnersReason()
    {
        var loadout = await NewLoadout();
        var list = await AddCollection(loadout.LoadoutId, "Witchcore", readOnly: true);
        var mod = await AddMod(loadout.LoadoutId, "InList", 1UL, [(PathA, "a")], parent: list);

        var plan = Remover.Plan(Request(loadout, PathA));

        var row = plan.Rows.Should().ContainSingle().Subject;
        row.CollectionName.Should().Be("Witchcore");
        row.LockReason.Should().Be(Ownership.WhyLocked(CollectionGroup.Load(Connection.Db, list.Value)));
        row.LockReason.Should().NotBeNull();
        plan.Locked.Should().ContainSingle();
        plan.Removable.Should().BeEmpty();
        row.GroupId.Should().Be(mod);
    }

    [Fact]
    public async Task ARowTwoLevelsUnderAModlistIsStillReportedLocked()
    {
        var loadout = await NewLoadout();
        var list = await AddCollection(loadout.LoadoutId, "FUS", readOnly: true);
        var mod = await AddMod(loadout.LoadoutId, "Mod", 1UL, [], parent: list);
        var inner = await AddMod(loadout.LoadoutId, "Inner", 1UL, [(PathA, "a")], parent: mod);

        var row = Remover.Plan(Request(loadout, PathA)).Rows.Should().ContainSingle().Subject;

        row.CollectionName.Should().Be("FUS", "the walk goes past the mod group to the list");
        row.LockReason.Should().NotBeNull();
        row.GroupId.Should().Be(inner);
    }

    [Fact]
    public async Task AnOverridesRowIsTheWinnerOverAModRowWhoseGroupHasTheTopPriority()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout.LoadoutId, "Top", 99UL, [(PathA, "mod")]);
        await AddOverridesFile(loadout, PathA, "overrides");

        var plan = Remover.Plan(Request(loadout, PathA));

        plan.Rows.Should().HaveCount(2);
        plan.Rows.Single(r => r.IsExternalChange).IsWinner.Should().BeTrue();
        plan.Rows.Single(r => !r.IsExternalChange).IsWinner.Should().BeFalse();
    }

    [Fact]
    public async Task AVanillaPathHasAnEmptyPlan()
    {
        var loadout = await NewLoadout();
        var basis = Connection.Db.BasisTxId;

        Remover.Plan(Request(loadout, Vanilla)).Rows.Should().BeEmpty();

        var act = () => Remover.Remove(Request(loadout, Vanilla), includeLocked: true);
        (await act.Should().ThrowAsync<FileRemovalRefusedException>()).Which.Message.Should().Contain("Nothing in the loadout provides");
        Connection.Db.BasisTxId.Should().Be(basis);
    }

    [Fact]
    public async Task GameInstalledContentIsBlockedAndNeverRemoved()
    {
        var loadout = await NewLoadout();
        var item = await AddGameInstalledItem(loadout, [(PathA, "creation")]);

        var plan = Remover.Plan(Request(loadout, PathA));
        plan.Blocked.Should().ContainSingle().Which.Blocker.Should().Contain("installed by the game");
        plan.Removable.Should().BeEmpty();

        var act = () => Remover.Remove(Request(loadout, PathA), includeLocked: true);
        (await act.Should().ThrowAsync<FileRemovalRefusedException>()).Which.Message.Should().Contain("installed by the game");
        RowsAt(PathA).Should().ContainSingle();
        item.Value.Should().NotBe(0UL);
    }

    [Fact]
    public async Task ADeleteMarkerIsBlockedInAnUnscopedPlanAndRemovableWhenScopedToItsGroup()
    {
        var loadout = await NewLoadout();
        var group = await AddOverridesDelete(loadout, PathA);

        var unscoped = Remover.Plan(Request(loadout, PathA));
        unscoped.Blocked.Should().ContainSingle().Which.Blocker.Should().Contain("already records a delete");
        unscoped.Removable.Should().BeEmpty();

        var scoped = Remover.Plan(Request(loadout, PathA) with { OnlyInGroups = [group] });
        scoped.Removable.Should().ContainSingle();
        scoped.Blocked.Should().BeEmpty();
    }

    [Fact]
    public async Task InstalledContentDetectionMatchesTheLoadoutPagesToggleLock()
    {
        var loadout = await NewLoadout();
        var item = await AddGameInstalledItem(loadout, [(PathA, "c")]);
        var mod = await AddMod(loadout.LoadoutId, "Plain", 1UL, [(PathB, "b")]);

        var itemEntity = LoadoutItem.Load(Connection.Db, item.Value);
        GameInstalledGroup.IsInstalledContent(itemEntity).Should().BeTrue("an item with a source id is the game's");
        var installedFile = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId)
            .Single(i => GameInstalledFile.Hash.TryGetValue(i, out _));
        GameInstalledGroup.IsInstalledContent(installedFile).Should().BeTrue("a file with a game hash is the game's");
        var plain = RowsAt(PathB).Single().AsLoadoutItem();
        GameInstalledGroup.IsInstalledContent(plain).Should().BeFalse();
        mod.Value.Should().NotBe(0UL);

        // A plain row whose parent is the game-installed group is still blocked by the service.
        var installedGroup = LoadoutItemGroup.Load(Connection.Db, itemEntity.ParentId.Value);
        await AddFileTo(LoadoutItemGroupId.From(installedGroup.Id), loadout.LoadoutId, new GamePath(LocationId.Game, "archive/pc/mod/c.archive"));
        Remover.Plan(Request(loadout, new GamePath(LocationId.Game, "archive/pc/mod/c.archive")))
            .Blocked.Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveDeletesTheRemovableRowsInOneTransactionAndLeavesTheGroup()
    {
        var loadout = await NewLoadout();
        var one = await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "1"), (PathB, "keep")]);
        var two = await AddMod(loadout.LoadoutId, "Two", 2UL, [(PathA, "2")]);

        var result = await Remover.Remove(Request(loadout, PathA), includeLocked: true);

        result.Removed.Should().BeEquivalentTo([PathA]);
        RowsAt(PathA).Should().BeEmpty();
        RowsAt(PathB).Should().ContainSingle();
        LoadoutItemGroup.Load(Connection.Db, one).IsValid().Should().BeTrue();
        LoadoutItemGroup.Load(Connection.Db, two).IsValid().Should().BeTrue();
        Connection.Db.BasisTxId.Value.Should().Be(result.RestorePoint.Value + 1, "the removal is one transaction right after the restore point");
    }

    [Fact]
    public async Task RemoveSkipsLockedRowsUnlessAsked()
    {
        var loadout = await NewLoadout();
        var list = await AddCollection(loadout.LoadoutId, "List", readOnly: true);
        await AddMod(loadout.LoadoutId, "InList", 1UL, [(PathA, "a")], parent: list);
        await AddMod(loadout.LoadoutId, "Mine", 2UL, [(PathA, "mine")]);

        var skipped = await Remover.Remove(Request(loadout, PathA), includeLocked: false);
        RowsAt(PathA).Should().ContainSingle("the list's row stays");
        skipped.Removed.Should().BeEquivalentTo([PathA]);

        await Remover.Remove(Request(loadout, PathA), includeLocked: true);
        RowsAt(PathA).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveRefusesWhenNothingWouldGo()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "a")]);
        var basis = Connection.Db.BasisTxId;

        var act = () => Remover.Remove(Request(loadout, PathB), includeLocked: true);

        (await act.Should().ThrowAsync<FileRemovalRefusedException>()).Which.Message.Should().Contain(PathB.Path.ToString());
        Connection.Db.BasisTxId.Should().Be(basis, "no transaction, restore point included, was written");
    }

    [Fact]
    public async Task RemoveRePlansSoARowAddedAfterTheCallersPlanIsAlsoRemoved()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "a")]);
        var request = Request(loadout, PathA);
        Remover.Plan(request).Rows.Should().ContainSingle();

        await AddMod(loadout.LoadoutId, "Two", 2UL, [(PathA, "late")]);
        await Remover.Remove(request, includeLocked: true);

        RowsAt(PathA).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveWritesARestorePointBeforeTheRemoval()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "a")]);
        var before = Undo.RevisionsFor(loadout.Id).Count;

        var result = await Remover.Remove(Request(loadout, PathA), includeLocked: true);

        var revisions = Undo.RevisionsFor(loadout.Id);
        revisions.Should().HaveCount(before + 1);
        revisions.Should().Contain(r => r.Revision.TxEntity.Value == result.RestorePoint.Value);
        var then = Connection.AsOf(result.RestorePoint);
        LoadoutItemWithTargetPath.FindByTargetPath(then, PathA.ToGamePathParentTuple(loadout.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task ARemovalThatFailsToCommitLeavesARestorePointThatRevertsToNothing()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "a")]);
        var failing = new LoadoutFileRemover(Connection, Ownership, NullLogger<LoadoutFileRemover>.Instance)
        {
            BeforeRemovalCommit = _ => throw new InvalidOperationException("injected"),
        };
        var before = Undo.RevisionsFor(loadout.Id).Count;

        var act = () => failing.Remove(Request(loadout, PathA), includeLocked: true);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var revisions = Undo.RevisionsFor(loadout.Id);
        revisions.Should().HaveCount(before + 1, "the marker landed");
        RowsAt(PathA).Should().ContainSingle("the removal did not");

        var ids = RowsAt(PathA).Select(r => r.Id).ToArray();
        await Undo.RevertTo(revisions.OrderBy(r => r.Revision.TxEntity).Last().Revision);
        RowsAt(PathA).Select(r => r.Id).Should().Equal(ids, "reverting to a marker nothing followed changes no row");
    }

    [Fact]
    public async Task RevertingToTheRestorePointBringsTheRowsBackUnderTheirIds()
    {
        var loadout = await NewLoadout();
        await AddMod(loadout.LoadoutId, "One", 1UL, [(PathA, "a")]);
        var ids = RowsAt(PathA).Select(r => r.Id).ToArray();

        var result = await Remover.Remove(Request(loadout, PathA), includeLocked: true);
        RowsAt(PathA).Should().BeEmpty();

        var revision = Undo.RevisionsFor(loadout.Id).Single(r => r.Revision.TxEntity.Value == result.RestorePoint.Value).Revision;
        await Undo.RevertTo(revision);

        RowsAt(PathA).Select(r => r.Id).Should().Equal(ids);
    }

    [Fact]
    public async Task ScopingToGroupsIgnoresRowsElsewhere()
    {
        var loadout = await NewLoadout();
        var a = await AddMod(loadout.LoadoutId, "A", 1UL, [(PathA, "a")]);
        await AddMod(loadout.LoadoutId, "B", 2UL, [(PathA, "b")]);

        var plan = Remover.Plan(Request(loadout, PathA) with { OnlyInGroups = [a] });

        plan.Rows.Should().ContainSingle().Which.GroupId.Should().Be(a);
        await Remover.Remove(Request(loadout, PathA) with { OnlyInGroups = [a] }, includeLocked: true);
        RowsAt(PathA).Should().ContainSingle().Which.AsLoadoutItem().ParentId.Value.Should().NotBe(a.Value);
    }

    private EntityId _loadout;

    private async Task<Loadout.ReadOnly> NewLoadout()
    {
        var loadout = await CreateLoadout();
        _loadout = loadout.Id;
        return loadout;
    }

    private static FileRemovalRequest Request(Loadout.ReadOnly loadout, params GamePath[] paths) => new(loadout.LoadoutId, paths);

    private LoadoutItemWithTargetPath.ReadOnly[] RowsAt(GamePath path)
    {
        return LoadoutItemWithTargetPath.FindByTargetPath(Connection.Db, path.ToGamePathParentTuple(_loadout)).ToArray();
    }

    private async Task Disable(LoadoutItemGroupId group)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(group.Value, LoadoutItem.Disabled, Null.Instance);
        await tx.Commit();
    }

    private async Task<LoadoutItemGroupId> AddCollection(LoadoutId loadout, string name, bool readOnly)
    {
        using var tx = Connection.BeginTransaction();
        _ = new CollectionGroup.New(tx, out var id)
        {
            IsReadOnly = readOnly,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, id)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, id) { Name = name, LoadoutId = loadout },
            },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[id].Value);
    }

    private async Task<LoadoutItemGroupId> AddMod(LoadoutId loadoutId, string name, ulong priority, (GamePath Path, string Content)[] files, LoadoutItemGroupId? parent = null)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadoutId, name, parent);
        foreach (var (path, content) in files)
            AddFile(tx, loadoutId, group, path, content);
        _ = new LoadoutItemGroupPriority.New(tx) { LoadoutId = loadoutId, TargetId = group, Priority = ConflictPriority.From(priority) };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[group].Value);
    }

    private async Task AddFileTo(LoadoutItemGroupId group, LoadoutId loadoutId, GamePath path)
    {
        using var tx = Connection.BeginTransaction();
        AddFile(tx, loadoutId, group, path, "x");
        await tx.Commit();
    }

    private async Task<LoadoutItemGroupId> AddOverridesFile(Loadout.ReadOnly loadout, GamePath path, string content)
    {
        var group = await EnsureOverrides(loadout);
        using var tx = Connection.BeginTransaction();
        AddFile(tx, loadout.LoadoutId, group, path, content);
        await tx.Commit();
        return group;
    }

    private async Task<LoadoutItemGroupId> AddOverridesDelete(Loadout.ReadOnly loadout, GamePath path)
    {
        var group = await EnsureOverrides(loadout);
        var fresh = Loadout.Load(Connection.Db, loadout.Id);
        using var tx = Connection.BeginTransaction();
        OverridesGroupItems.AddReifiedDelete(tx, fresh, LoadoutOverridesGroupId.From(group.Value), path);
        await tx.Commit();
        return group;
    }

    private async Task<LoadoutItemGroupId> EnsureOverrides(Loadout.ReadOnly loadout)
    {
        var synchronizer = (ALoadoutSynchronizer)Synchronizer;
        using (var create = Connection.BeginTransaction())
        {
            synchronizer.GetOrCreateOverridesGroup(create, loadout);
            await create.Commit();
        }
        return LoadoutItemGroupId.From(LoadoutOverridesGroup.FindByOverridesFor(Connection.Db, loadout.Id).Single().Id);
    }

    private async Task<LoadoutItemGroupId> AddGameInstalledItem(Loadout.ReadOnly loadout, (GamePath Path, string Content)[] files)
    {
        using var tx = Connection.BeginTransaction();
        var group = ALoadoutSynchronizer.CreateGameInstalledGroup(tx, loadout);
        _ = new GameInstalledItem.New(tx, out var itemId)
        {
            Source = "creation",
            SourceId = "creation-1",
            Confidence = "GameRecord",
            CanBeClaimed = false,
            DepotShipped = false,
            LoadoutItemGroup = new LoadoutItemGroup.New(tx, itemId)
            {
                IsGroup = true,
                LoadoutItem = new LoadoutItem.New(tx, itemId) { Name = "A Creation", LoadoutId = loadout.Id, ParentId = group },
            },
        };
        foreach (var (path, content) in files)
            AddFile(tx, loadout.LoadoutId, LoadoutItemGroupId.From(itemId), path, content);
        _ = new GameInstalledFile.New(tx, out var fileId)
        {
            Path = (files[0].Path).ToGamePathParentTuple(loadout.Id),
            Hash = default,
            Size = default,
            LoadoutItem = new LoadoutItem.New(tx, fileId) { Name = "f", LoadoutId = loadout.Id, ParentId = LoadoutItemGroupId.From(itemId) },
        };
        var result = await tx.Commit();
        return LoadoutItemGroupId.From(result[itemId].Value);
    }
}
