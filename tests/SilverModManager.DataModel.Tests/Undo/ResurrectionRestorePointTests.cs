using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers.Conflicts;
using NexusMods.DataModel.Undo;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.DataModel.Tests.Undo;

/// <summary>
/// Items removed since a revision come back as the same entities, items added since go away, and going
/// back and forth between revisions lands on identical state.
/// </summary>
public class ResurrectionRestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<ResurrectionRestorePointTests>(helper)
{
    private static readonly (string Path, string Content)[] Files =
    [
        ("mods/resurrected/a.txt", "content of a"),
        ("mods/resurrected/b.txt", "content of b"),
    ];

    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();

    [Fact]
    public async Task AnItemRemovedSinceComesBackUnderItsOriginalIds()
    {
        var loadout = await CreateLoadout();
        var removed = await AddPrioritisedLinkedItem(loadout.LoadoutId, "Removed later", Files);
        var kept = await AddPrioritisedLinkedItem(loadout.LoadoutId, "Kept", []);
        await StampRevision(loadout.Id);
        var restorePoint = LastRevision(loadout.Id);

        var filesBefore = FilesOf(removed.Group);
        var prioritiesBefore = Priorities(loadout.LoadoutId);
        prioritiesBefore.Should().HaveCount(2);

        await LoadoutManager.RemoveItems([removed.Group]);
        await StampRevision(loadout.Id);
        LoadoutItemGroup.Load(Connection.Db, removed.Group).IsValid().Should().BeFalse();
        Priorities(loadout.LoadoutId).Should().NotEqual(prioritiesBefore, "removing an item rebalances the remaining priorities");

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        result.DanglingReferences.Should().BeEmpty();
        var group = LoadoutItemGroup.Load(Connection.Db, removed.Group);
        group.IsValid().Should().BeTrue("the group comes back under its original id");
        FilesOf(removed.Group).Should().BeEquivalentTo(filesBefore);
        filesBefore.Should().HaveCount(2);
        LibraryLinkedLoadoutItem.Load(Connection.Db, removed.Group).LibraryItem.IsValid().Should().BeTrue();

        var priority = LoadoutItemGroupPriority.Load(Connection.Db, removed.Priority);
        priority.IsValid().Should().BeTrue("the priority row comes back under its original id");
        priority.Priority.Should().Be(prioritiesBefore[removed.Group]);
        Priorities(loadout.LoadoutId).Should().BeEquivalentTo(prioritiesBefore);
        LoadoutItemGroup.Load(Connection.Db, kept.Group).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task AnItemAddedSinceIsRemovedWithItsFilesAndPriority()
    {
        var loadout = await CreateLoadout();
        await AddPrioritisedLinkedItem(loadout.LoadoutId, "Before", []);
        await StampRevision(loadout.Id);
        var restorePoint = LastRevision(loadout.Id);
        var prioritiesBefore = Priorities(loadout.LoadoutId);

        var added = await AddPrioritisedLinkedItem(loadout.LoadoutId, "Added later", Files);
        await StampRevision(loadout.Id);
        var children = LoadoutItemGroup.Load(Connection.Db, added.Group).Children.Select(child => child.Id).ToArray();
        children.Should().HaveCount(2);

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        LoadoutItemGroup.Load(Connection.Db, added.Group).IsValid().Should().BeFalse();
        children.Should().OnlyContain(child => !LoadoutItem.Load(Connection.Db, child).IsValid());
        LoadoutItemGroupPriority.Load(Connection.Db, added.Priority).IsValid().Should().BeFalse();
        Priorities(loadout.LoadoutId).Should().BeEquivalentTo(prioritiesBefore);
    }

    [Fact]
    public async Task RevertingBackAndForthEndsInTheSameState()
    {
        var loadout = await CreateLoadout();
        await AddPrioritisedLinkedItem(loadout.LoadoutId, "First", Files);
        await StampRevision(loadout.Id);
        var older = LastRevision(loadout.Id);

        var second = await AddPrioritisedLinkedItem(loadout.LoadoutId, "Second", []);
        await AddNexusCollection(loadout.LoadoutId, "A collection", collectionId: 1, revisionId: 11, childCount: 2);
        await LoadoutManager.RemoveItems([second.Group]);
        await AddPrioritisedLinkedItem(loadout.LoadoutId, "Third", Files);
        await StampRevision(loadout.Id);
        var newest = LastRevision(loadout.Id);

        var before = DatomsOf(Connection.Db, OwnedIds(Connection.Db, loadout.Id));
        before.Should().NotBeEmpty();

        var toOlder = await UndoService.RevertTo(older);
        toOlder.Residual.Should().BeEmpty();
        DatomsOf(Connection.Db, OwnedIds(Connection.Db, loadout.Id)).Should().NotBeEquivalentTo(before);

        var toNewest = await UndoService.RevertTo(newest);

        toNewest.Residual.Should().BeEmpty();
        DatomsOf(Connection.Db, OwnedIds(Connection.Db, loadout.Id)).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task ADeletedLoadoutCanNotBeReverted()
    {
        var loadout = await CreateLoadout();
        await AddLinkedItem(loadout.LoadoutId, "Item");
        await StampRevision(loadout.Id);
        var restorePoint = LastRevision(loadout.Id);

        await DeleteLoadoutAsync(loadout.LoadoutId);
        var basis = Connection.Db.BasisTxId;

        var revert = () => UndoService.RevertTo(restorePoint);

        await revert.Should().ThrowAsync<InvalidOperationException>();
        Connection.Db.BasisTxId.Should().Be(basis, "nothing was written");
    }

    private LoadoutRevision LastRevision(EntityId loadout) =>
        UndoService.RevisionsFor(loadout).OrderBy(r => r.Revision.TxEntity).Last().Revision;

    private async Task<(LoadoutItemGroupId Group, LoadoutItemGroupPriorityId Priority)> AddPrioritisedLinkedItem(
        LoadoutId loadout, string name, (string Path, string Content)[] files)
    {
        var archive = await CreateLibraryArchive(Connection, $"{name}.zip");
        var group = await AddLinkedItem(loadout, name, archive.Id, files);
        return (group, await AddPriority(loadout, group));
    }

    private Dictionary<LoadoutItemGroupId, ConflictPriority> Priorities(LoadoutId loadout) =>
        LoadoutItemGroupPriority.FindByLoadout(Connection.Db, loadout).ToDictionary(p => p.TargetId, p => p.Priority);

    private (string Path, NexusMods.Hashing.xxHash3.Hash Hash)[] FilesOf(LoadoutItemGroupId group) =>
        LoadoutItemGroup.Load(Connection.Db, group).Children
            .Select(child => LoadoutFile.Load(Connection.Db, child.Id))
            .Where(file => file.IsValid())
            .Select(file => (file.AsLoadoutItemWithTargetPath().TargetPath.Item3.ToString(), file.Hash))
            .ToArray();
}
