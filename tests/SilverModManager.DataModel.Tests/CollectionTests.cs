using FluentAssertions;
using SilverModManager.Abstractions.Collections;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Collections;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests;

public class CollectionTests(ITestOutputHelper helper) : ANexusCollectionTest<CollectionTests>(helper)
{
    private const string SourceName = "Aesthetic Valley | Witchcore";
    private const string CloneName = "[Copy Of] Aesthetic Valley | Witchcore";

    [Fact]
    public async Task CanMakeNexusModsCollectionsEditable()
    {
        var loadout = await CreateLoadout();
        await AddNexusCollection(loadout, SourceName, collectionId: 1, revisionId: 11, childCount: 3, withBundledChild: true);
        await AddNexusCollection(loadout, "Other Collection", collectionId: 2, revisionId: 22, childCount: 2);

        var conn = Connection;
        var coll = FindCollection(SourceName);
        var other = FindCollection("Other Collection");
        var sourceChildren = ChildIds(coll.Id);
        sourceChildren.Should().HaveCount(4);

        var newId = await CollectionCreator.MakeEditableLocalCollection(LoadoutManager, conn, coll.AsCollectionGroup(), CloneName);
        var newColl = CollectionGroup.Load(conn.Db, newId);

        newColl.AsLoadoutItemGroup().AsLoadoutItem().Name.Should().Be(CloneName);
        newColl.IsReadOnly.Should().BeFalse();

        NexusCollectionLoadoutGroup.Collection.Contains(newColl).Should().BeFalse();
        NexusCollectionLoadoutGroup.Collection.Contains(coll).Should().BeTrue();

        NexusCollectionLoadoutGroup.Revision.Contains(newColl).Should().BeFalse();
        NexusCollectionLoadoutGroup.Revision.Contains(coll).Should().BeTrue();

        NexusCollectionLoadoutGroup.LibraryFile.Contains(newColl).Should().BeFalse();
        NexusCollectionLoadoutGroup.LibraryFile.Contains(coll).Should().BeTrue();

        conn.Query<EntityId>($"SELECT Id FROM mdb_NexusCollectionItemLoadoutGroup(Db => {conn}) WHERE Parent = {newColl.Id}")
            .Should()
            .HaveCount(0);

        conn.Query<EntityId>($"SELECT Id FROM mdb_LoadoutItemGroup(Db => {conn}) WHERE Parent = {newColl.Id}")
            .Should()
            .HaveCountGreaterThan(0);

        // The clone keeps every child, but none of them points at the Nexus collection any more.
        var cloneChildren = ChildIds(newColl.Id);
        cloneChildren.Should().HaveCount(sourceChildren.Count);
        foreach (var child in cloneChildren.Select(Item))
        {
            NexusCollectionItemLoadoutGroup.Download.Contains(child).Should().BeFalse();
            NexusCollectionItemLoadoutGroup.IsRequired.Contains(child).Should().BeFalse();
            NexusCollectionReplicatedLoadoutGroup.Replicated.Contains(child).Should().BeFalse();
            NexusCollectionBundledLoadoutGroup.BundleDownload.Contains(child).Should().BeFalse();
            NexusCollectionBundledLoadoutGroup.CollectionLibraryFile.Contains(child).Should().BeFalse();
        }

        // The bundled files were copied into a manually created archive that the clone links to.
        var bundledClone = LoadoutItem.Load(conn.Db, cloneChildren.Single(id => LoadoutItem.Load(conn.Db, id).Name.EndsWith("bundled")));
        LibraryLinkedLoadoutItem.LibraryItemId.Contains(bundledClone).Should().BeTrue();
        var linked = LibraryLinkedLoadoutItem.Load(conn.Db, bundledClone.Id);
        var archive = ManuallyCreatedArchive.Load(conn.Db, linked.LibraryItemId);
        archive.IsValid().Should().BeTrue();
        archive.Source.Should().Be(ManuallyCreatedArchive.CreationSource.CollectionBundled);

        // c.txt repeats a.txt's content and is deduplicated.
        var entries = archive.AsLibraryArchive().Children.ToArray();
        entries.Should().HaveCount(2);
        archive.AsLibraryArchive().AsLibraryFile().Size.Should().Be(entries.Aggregate(Size.Zero, (sum, e) => sum + e.AsLibraryFile().Size));
        archive.AsLibraryArchive().AsLibraryFile().Size.Should().Be(Size.FromLong("bundled content a".Length + "bundled content bb".Length));

        // The source and the unrelated collection are untouched. Reload them: a ReadOnly keeps the db it was loaded from.
        coll = FindCollection(SourceName);
        other = FindCollection("Other Collection");
        NexusCollectionLoadoutGroup.Collection.Contains(coll).Should().BeTrue();
        NexusCollectionLoadoutGroup.Revision.Contains(coll).Should().BeTrue();
        NexusCollectionLoadoutGroup.LibraryFile.Contains(coll).Should().BeTrue();
        ChildIds(coll.Id).Should().BeEquivalentTo(sourceChildren);
        foreach (var child in sourceChildren.Select(Item))
        {
            NexusCollectionItemLoadoutGroup.Download.Contains(child).Should().BeTrue();
            NexusCollectionItemLoadoutGroup.IsRequired.Contains(child).Should().BeTrue();
        }
        sourceChildren.Select(Item).Count(c => NexusCollectionReplicatedLoadoutGroup.Replicated.Contains(c)).Should().Be(1);
        sourceChildren.Select(Item).Count(c => NexusCollectionBundledLoadoutGroup.BundleDownload.Contains(c)).Should().Be(1);
        sourceChildren.Select(Item).Count(c => NexusCollectionBundledLoadoutGroup.CollectionLibraryFile.Contains(c)).Should().Be(1);

        var otherChildren = ChildIds(other.Id).Select(Item).ToList();
        otherChildren.Should().HaveCount(2);
        otherChildren.Should().OnlyContain(c => NexusCollectionItemLoadoutGroup.Download.Contains(c));
        NexusCollectionLoadoutGroup.Collection.Contains(other).Should().BeTrue();
        NexusCollectionLoadoutGroup.Revision.Contains(other).Should().BeTrue();
        NexusCollectionLoadoutGroup.LibraryFile.Contains(other).Should().BeTrue();
    }

    private NexusCollectionLoadoutGroup.ReadOnly FindCollection(string name)
    {
        var db = Connection.Db;
        return Connection.Query<EntityId>($"SELECT Id FROM mdb_NexusCollectionLoadoutGroup(Db => {db})")
            .Select(id => NexusCollectionLoadoutGroup.Load(db, id))
            .Single(c => c.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name == name);
    }

    private LoadoutItem.ReadOnly Item(EntityId id) => LoadoutItem.Load(Connection.Db, id);

    private List<EntityId> ChildIds(EntityId parent)
    {
        var db = Connection.Db;
        return Connection.Query<EntityId>($"SELECT Id FROM mdb_LoadoutItemGroup(Db => {db}) WHERE Parent = {parent}").ToList();
    }
}
