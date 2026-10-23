using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.DataModel.Undo;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.Tests.Undo;

/// <summary>
/// Reverts a loadout through a history that was written to, flushed and compacted on a RocksDB store.
/// </summary>
/// <remarks>
/// The original test replayed a real 63 MB user database. Reverting in it crashed because of a sorting
/// bug in MnemonicDB that shows up after the on-disk store is compacted, and the crash was fixed by
/// bumping the MnemonicDB package. This version builds a comparable history in code instead, so it
/// guards <see cref="UndoService.RevertTo"/> against regressions after a flush and compaction. It is
/// not shown to reproduce that exact MnemonicDB bug, and the bug can't be re-introduced from app code.
/// </remarks>
public class OnDiskRestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<OnDiskRestorePointTests>(helper)
{
    // Field initializers run before the base constructor, which builds the host and calls AddServices.
    private readonly AbsolutePath _dbPath = NexusMods.Paths.FileSystem.Shared
        .GetKnownPath(KnownPath.EntryDirectory)
        .Combine($"RestorePointTests-{Guid.NewGuid()}/MnemonicDB.rocksdb");

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        base.AddServices(services);

        // The default test setup keeps the database in memory, which would never touch the compaction path.
        _dbPath.CreateDirectory();
        services.AddDatomStoreSettings(new DatomStoreSettings { Path = _dbPath });
        return services;
    }

    [Fact]
    public async Task UndoDoesntCorruptTheDatabase()
    {
        var undoService = ServiceProvider.GetRequiredService<UndoService>();
        var loadout = await CreateLoadout();
        var loadoutId = loadout.Id;

        // Revision 0 comes from creating the loadout.
        var collectionA = await AddNexusCollection(loadout, "Collection A", collectionId: 1, revisionId: 11, childCount: 3);
        await StampRevision(loadoutId);
        await AddNexusCollection(loadout, "Collection B", collectionId: 2, revisionId: 22, childCount: 2);
        await StampRevision(loadoutId);

        // A second group over the same collection and revision as A.
        await AddNexusCollection(loadout, "Collection A (again)", collectionId: 0, revisionId: 0, childCount: 3, duplicateOf: collectionA);
        await StampRevision(loadoutId);

        await LoadoutManager.RemoveCollection(collectionA.AsCollectionGroup());
        await StampRevision(loadoutId);

        var plain1 = await AddLinkedItem(loadout, "Plain 1");
        await AddLinkedItem(loadout, "Plain 2");
        await StampRevision(loadoutId);

        await LoadoutManager.RemoveItems([plain1]);
        await StampRevision(loadoutId);

        var collectionB = SilverModManager.Abstractions.Collections.NexusCollectionLoadoutGroup.All(Connection.Db)
            .Single(c => c.AsCollectionGroup().AsLoadoutItemGroup().AsLoadoutItem().Name == "Collection B");
        await LoadoutManager.RemoveCollection(collectionB.AsCollectionGroup());
        await StampRevision(loadoutId);

        await Connection.FlushAndCompact();
        _dbPath.EnumerateFiles().Should().NotBeEmpty("the test has to run against the on-disk store");

        var original = undoService.RevisionsFor(loadoutId).OrderBy(r => r.Revision.TxEntity).ToArray();
        original.Length.Should().BeGreaterThanOrEqualTo(7, "the revision indexes used below must exist");
        original.Select(r => r.ModCount).Should().Equal([0, 3, 5, 8, 5, 7, 6, 4]);

        // Alternate between an early and a late revision of the original history.
        var targets = new[] { original[1], original[6] };
        for (var i = 0; i < 4; i++)
        {
            var target = targets[i % 2];
            var result = await undoService.RevertTo(target.Revision);

            result.Residual.Should().BeEmpty();

            LinkedItemCount(loadoutId).Should().Be(target.ModCount, $"revert {i} went to revision with {target.ModCount} items");
            undoService.RevisionsFor(loadoutId).Should().HaveCount(original.Length + i + 1);
        }

        await Connection.FlushAndCompact();

        Loadout.Load(Connection.Db, loadoutId).IsValid().Should().BeTrue();
        var finalRevisions = undoService.RevisionsFor(loadoutId);
        finalRevisions.Should().NotBeEmpty();
        finalRevisions.Select(r => r.ModCount).Should().OnlyContain(c => c >= 0);
    }

    private int LinkedItemCount(EntityId loadout)
    {
        var db = Connection.Db;
        return (int)Connection.Query<long>($"SELECT COUNT(*) FROM mdb_LibraryLinkedLoadoutItem(Db=>{db}) WHERE Loadout = {loadout}").First();
    }
}
