using System.Text;
using FluentAssertions;
using SilverModManager.Abstractions.Library;
using SilverModManager.App.GarbageCollection.Nx;
using SilverModManager.DataModel;
using SilverModManager.Games.TestFramework;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Library;
using SilverModManager.StandardGameLocators.TestHelpers.StubbedGames;
using static SilverModManager.App.GarbageCollection.DataModel.Tests.FindUsedFiles.Helpers;

namespace SilverModManager.App.GarbageCollection.DataModel.Tests.FindUsedFiles;

/// <summary>
/// A library file that is not an archive (an .esp added on its own) keeps its bytes in the store under its own hash,
/// so it is a root; so are bytes a running job has pinned before any row references them.
/// </summary>
public class MarkPlainLibraryFilesTest(IServiceProvider serviceProvider) : AGameTest<StubbedGame>(serviceProvider)
{
    private async Task<Hash> StoreBytes(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = bytes.xxHash3();
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(text, new MemoryStream(bytes)), hash, Size.FromLong(bytes.Length))]);
        return hash;
    }

    private ArchiveGarbageCollector<NxParsedHeaderState, FileEntryWrapper> MarkedCollector(Hash storedHash)
    {
        ((NxFileStore)FileStore).TryGetLocation(storedHash, out var archive, out _).Should().BeTrue();
        var gc = new ArchiveGarbageCollector<NxParsedHeaderState, FileEntryWrapper>();
        gc.AddArchive(archive, GetParsedNxHeader(archive));
        DataStoreReferenceMarker.MarkUsedFiles(Connection, gc);
        return gc;
    }

    [Fact]
    public async Task APlainLibraryFileIsMarkedAsUsedUntilItIsRemoved()
    {
        var text = $"plain plugin {Guid.NewGuid()}";
        var hash = await StoreBytes(text);
        IsFileReferenced(MarkedCollector(hash), hash).Should().BeFalse("nothing references the bytes yet");

        using var tx = Connection.BeginTransaction();
        _ = new LibraryFile.New(tx, out var id)
        {
            FileName = "Plugin.esp",
            Hash = hash,
            Size = Size.FromLong(text.Length),
            LibraryItem = new LibraryItem.New(tx, id) { Name = "Plugin.esp" },
        };
        var committed = await tx.Commit();

        IsFileReferenced(MarkedCollector(hash), hash).Should().BeTrue("a plain library file is a root");

        using var retract = Connection.BeginTransaction();
        retract.Delete(committed[id], recursive: true);
        await retract.Commit();

        IsFileReferenced(MarkedCollector(hash), hash).Should().BeFalse("the library file is gone");
    }

    [Fact]
    public async Task APinnedHashIsMarkedAsUsedUntilTheScopeIsDisposed()
    {
        var hash = await StoreBytes($"pinned bytes {Guid.NewGuid()}");

        var pin = FileStorePins.Pin(hash);
        IsFileReferenced(MarkedCollector(hash), hash).Should().BeTrue("a job has pinned it");

        pin.Dispose();
        IsFileReferenced(MarkedCollector(hash), hash).Should().BeFalse("the pin is released");
    }
}
