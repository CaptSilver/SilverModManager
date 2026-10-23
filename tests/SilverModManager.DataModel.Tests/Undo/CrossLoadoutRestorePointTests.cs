using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Undo;

/// <summary>
/// Reverting one loadout must leave everything it does not own alone: other loadouts, the library,
/// collection metadata and the install's disk state.
/// </summary>
public class CrossLoadoutRestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<CrossLoadoutRestorePointTests>(helper)
{
    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();

    [Fact]
    public async Task RevertingOneLoadoutLeavesTheOtherLoadoutAndTheLibraryAlone()
    {
        var (l1, l2) = await CreateTwoLoadouts();
        var restorePoint = FirstRevision(l1);

        var item1 = await AddLinkedItem(l1, "L1 item");
        var item2 = await AddLinkedItem(l2, "L2 item");
        var unrelated = await CreateLibraryArchive(Connection, "unrelated.zip");
        await StampRevision(l1);

        var libraryItem2 = LibraryLinkedLoadoutItem.Load(Connection.Db, item2).LibraryItemId;
        var untouched = IdsOutsideLoadout(l1).ToHashSet();
        untouched.UnionWith(OwnedIds(Connection.Db, l2));
        untouched.Add(libraryItem2);
        untouched.Add(unrelated.Id);
        var before = DatomsOf(Connection.Db, untouched);
        before.Should().NotBeEmpty();

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        LibraryLinkedLoadoutItem.Load(Connection.Db, item1).IsValid().Should().BeFalse("the item was added after the restore point");
        OwnedIds(Connection.Db, l1).Should().BeEquivalentTo(OwnedAt(restorePoint, l1));
        DatomsOf(Connection.Db, untouched).Should().BeEquivalentTo(before);
        LibraryLinkedLoadoutItem.Load(Connection.Db, item2).IsValid().Should().BeTrue();
    }

    [Fact]
    public async Task AnArchiveLinkedFromBothLoadoutsIsNotTouchedWhenOneLoadoutReverts()
    {
        var (l1, l2) = await CreateTwoLoadouts();
        var shared = await CreateLibraryArchive(Connection, "shared.zip");
        var item2 = await AddLinkedItem(l2, "L2 link", shared.Id);
        await StampRevision(l1);
        var restorePoint = LastRevision(l1);

        var item1 = await AddLinkedItem(l1, "L1 link", shared.Id);
        await StampRevision(l1);

        // Changes to things L1 does not own, made after the restore point, must survive too.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(item2.Value, LoadoutItem.Name, "L2 link, renamed");
            tx.Add(shared.Id, LibraryItem.Name, "shared.zip, renamed");
            await tx.Commit();
        }

        var untouched = OwnedIds(Connection.Db, l2).Append(shared.Id).ToList();
        var before = DatomsOf(Connection.Db, untouched);

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        LibraryLinkedLoadoutItem.Load(Connection.Db, item1).IsValid().Should().BeFalse();
        OwnedIds(Connection.Db, l1).Should().BeEquivalentTo(OwnedAt(restorePoint, l1));
        LibraryLinkedLoadoutItem.Load(Connection.Db, item2).IsValid().Should().BeTrue();
        DatomsOf(Connection.Db, untouched).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task CollectionMetadataSharedWithAnotherLoadoutIsNotTouchedWhenOneLoadoutReverts()
    {
        var (l1, l2) = await CreateTwoLoadouts();
        var inL2 = await AddNexusCollection(l2, "Shared collection", collectionId: 1, revisionId: 11, childCount: 2);
        await StampRevision(l1);
        var restorePoint = LastRevision(l1);

        var collectionOnL1 = await AddNexusCollection(l1, "Shared collection (L1)", collectionId: 0, revisionId: 0, childCount: 2, duplicateOf: inL2);
        await StampRevision(l1);

        // Changes to things L1 does not own, made after the restore point, must survive too.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(inL2.Id, LoadoutItem.Name, "Shared collection, renamed");
            tx.Add(NexusCollectionLoadoutGroup.CollectionId.Get(inL2), CollectionMetadata.Name, "Shared collection, renamed upstream");
            await tx.Commit();
        }

        var db = Connection.Db;
        var collectionId = NexusCollectionLoadoutGroup.CollectionId.Get(inL2);
        var revisionId = NexusCollectionLoadoutGroup.RevisionId.Get(inL2);
        var libraryFileId = NexusCollectionLoadoutGroup.LibraryFileId.Get(inL2);
        var downloadsOfL2 = inL2.AsCollectionGroup().AsLoadoutItemGroup().Children
            .Select(child => NexusCollectionItemLoadoutGroup.Load(db, child.Id).DownloadId.Value);

        var untouched = OwnedIds(db, l2)
            .Concat([collectionId, revisionId, libraryFileId])
            .Concat(downloadsOfL2)
            .ToList();
        var before = DatomsOf(db, untouched);

        var l1Owned = OwnedIds(db, l1);
        l1Owned.Should().Contain(collectionOnL1.Id, "the group was added to L1");

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        NexusCollectionLoadoutGroup.Load(Connection.Db, collectionOnL1.Id).IsValid().Should().BeFalse("L1's group was added after the restore point");
        OwnedIds(Connection.Db, l1).Should().BeEquivalentTo(OwnedAt(restorePoint, l1), "L1's group and its children are gone");
        DatomsOf(Connection.Db, untouched).Should().BeEquivalentTo(before);
        NexusCollectionLoadoutGroup.Load(Connection.Db, inL2.Id).IsValid().Should().BeTrue();
    }

    private async Task<(LoadoutId L1, LoadoutId L2)> CreateTwoLoadouts()
    {
        var first = await CreateLoadout();
        var second = await LoadoutManager.CreateLoadout(GameInstallation, "Second loadout");
        return (first.LoadoutId, second.LoadoutId);
    }

    private HashSet<EntityId> OwnedAt(LoadoutRevision revision, LoadoutId loadout) =>
        OwnedIds(Connection.AsOf(TxId.From(revision.TxEntity.Value)), loadout);

    private LoadoutRevision FirstRevision(LoadoutId loadout) =>
        UndoService.RevisionsFor(loadout).OrderBy(r => r.Revision.TxEntity).First().Revision;

    private LoadoutRevision LastRevision(LoadoutId loadout) =>
        UndoService.RevisionsFor(loadout).OrderBy(r => r.Revision.TxEntity).Last().Revision;

    /// <summary>
    /// The install's metadata row and every disk state entry: shared across loadouts and never owned by one.
    /// </summary>
    private IEnumerable<EntityId> IdsOutsideLoadout(LoadoutId loadout)
    {
        var db = Connection.Db;
        yield return Loadout.Load(db, loadout).InstallationId;
        foreach (var entry in DiskStateEntry.All(db))
            yield return entry.Id;
    }
}
