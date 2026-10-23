using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.GC;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.NexusModsLibrary;
using NexusMods.App.GarbageCollection;
using NexusMods.App.GarbageCollection.DataModel;
using NexusMods.App.GarbageCollection.Nx;
using NexusMods.Archives.Nx.Headers;
using NexusMods.DataModel.Undo;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Paths.Extensions.Nx.FileProviders;
using NexusMods.Sdk.FileStore;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Loadouts;
using System.Text;

namespace NexusMods.DataModel.Tests.Undo;

/// <summary>
/// A revert restores items faithfully even when what they point at is gone, and says which references it left dangling.
/// </summary>
public class DanglingReferenceRestorePointTests(ITestOutputHelper helper) : ANexusCollectionTest<DanglingReferenceRestorePointTests>(helper)
{
    private static readonly (string Path, string Content)[] Files =
    [
        ("mods/dangling/a.txt", "content of a"),
        ("mods/dangling/b.txt", "content of b"),
    ];

    private UndoService UndoService => ServiceProvider.GetRequiredService<UndoService>();

    [Fact]
    public async Task AnItemWhoseLibraryItemWasRemovedComesBackAndTheReferenceIsReported()
    {
        var loadout = await CreateLoadout();
        var archive = await CreateLibraryArchive(Connection, "dangling.zip");
        var hashes = await BackUp(Files);
        var item = await AddLinkedItem(loadout.LoadoutId, "Linked item", archive.Id, Files);
        await StampRevision(loadout.Id);
        var restorePoint = UndoService.RevisionsFor(loadout.Id).OrderBy(r => r.Revision.TxEntity).Last().Revision;

        await LibraryService.RemoveLibraryItems([archive.AsLibraryFile().AsLibraryItem()], GarbageCollectorRunMode.DoNotRun);
        LibraryLinkedLoadoutItem.Load(Connection.Db, item).IsValid().Should().BeFalse("removing the library item removes what was installed from it");
        hashes.Should().OnlyContain(hash => !IsReferenced(hash), "nothing references the files while the item is gone");

        var result = await UndoService.RevertTo(restorePoint);

        result.Residual.Should().BeEmpty();
        var restored = LibraryLinkedLoadoutItem.Load(Connection.Db, item);
        restored.IsValid().Should().BeTrue();
        var restoredFiles = restored.AsLoadoutItemGroup().Children
            .Select(child => LoadoutFile.Load(Connection.Db, child.Id))
            .Select(file => (Path: file.AsLoadoutItemWithTargetPath().TargetPath.Item3.ToString(), file.Hash))
            .ToArray();
        restoredFiles.Should().BeEquivalentTo(Files.Zip(hashes, (file, hash) => (file.Path, Hash: hash)));

        restored.LibraryItem.IsValid().Should().BeFalse("the library item was removed and is not recreated");
        var libraryItemAttribute = Connection.AttributeCache.GetAttributeId(LibraryLinkedLoadoutItem.LibraryItem.Id);
        result.DanglingReferences.Should().Equal([(item.Value, libraryItemAttribute, archive.Id)]);

        restored.LibraryItem.TryGetAsNexusModsLibraryItem(out _).Should().BeFalse("an invalid library item is not a Nexus Mods one");
        hashes.Should().OnlyContain(hash => IsReferenced(hash), "the restored files keep their data alive");
    }

    [Fact]
    public async Task ARevertThatBringsBackNothingReportsNoDanglingReferences()
    {
        var loadout = await CreateLoadout();
        var archive = await CreateLibraryArchive(Connection, "dangling.zip");
        var item = await AddLinkedItem(loadout.LoadoutId, "Linked item", archive.Id, Files);
        await StampRevision(loadout.Id);
        var firstRestorePoint = UndoService.RevisionsFor(loadout.Id).OrderBy(r => r.Revision.TxEntity).Last().Revision;

        await LibraryService.RemoveLibraryItems([archive.AsLibraryFile().AsLibraryItem()], GarbageCollectorRunMode.DoNotRun);
        var first = await UndoService.RevertTo(firstRestorePoint);
        first.DanglingReferences.Should().ContainSingle("the item is back and its library item is not");

        // The revert records a revision of its own. Add something, then go back to that revision.
        var restorePoint = UndoService.RevisionsFor(loadout.Id).OrderBy(r => r.Revision.TxEntity).Last().Revision;
        var later = await AddLinkedItem(loadout.LoadoutId, "Added later");
        await StampRevision(loadout.Id);

        var second = await UndoService.RevertTo(restorePoint);

        second.Residual.Should().BeEmpty();
        LibraryLinkedLoadoutItem.Load(Connection.Db, later).IsValid().Should().BeFalse();
        LibraryLinkedLoadoutItem.Load(Connection.Db, item).LibraryItem.IsValid().Should().BeFalse("the reference was dangling before this revert too");
        second.DanglingReferences.Should().BeEmpty("this revert asserted no reference");
    }

    private async Task<Hash[]> BackUp((string Path, string Content)[] files)
    {
        var entries = files.Select(file =>
        {
            var bytes = Encoding.UTF8.GetBytes(file.Content);
            return new ArchivedFileEntry(new MemoryStreamFactory(file.Path, new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length));
        }).ToArray();
        await FileStore.BackupFiles(entries);
        return entries.Select(entry => entry.Hash).ToArray();
    }

    /// <summary>
    /// Whether the garbage collector would keep the data for the hash, going by what the database references.
    /// </summary>
    private bool IsReferenced(Hash hash)
    {
        ((NxFileStore)FileStore).TryGetLocation(hash, out var archivePath, out _).Should().BeTrue();
        var gc = new ArchiveGarbageCollector<NxParsedHeaderState, FileEntryWrapper>();
        gc.AddArchive(archivePath, new NxParsedHeaderState(HeaderParser.ParseHeader(new FromAbsolutePathProvider { FilePath = archivePath })));
        DataStoreReferenceMarker.MarkUsedFiles(Connection, gc);

        // The collector only calls back for archives that hold unreferenced files, and lists those files.
        var unreferenced = new HashSet<Hash>();
        gc.CollectGarbage(progress: null, (_, _, toRemove, _) => unreferenced.UnionWith(toRemove));
        return !unreferenced.Contains(hash);
    }
}
