using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Tests.Acquire;

/// <summary>
/// The original of a game file is found in the first disk state the app recorded, which is read as of an old
/// transaction: rows from before disk state carried a hash kind have only the xxHash3.
/// </summary>
public class GameFileSourceLookupTests(ITestOutputHelper helper) : AcquireTestBase<GameFileSourceLookupTests>(helper)
{
    private GameFileSourceLookup Lookup(Loadout.ReadOnly loadout)
        => new(loadout, Connection, FileStore, ServiceProvider.GetRequiredService<SilverModManager.Abstractions.Games.FileHashes.IFileIdentityResolver>(),
        ServiceProvider.GetRequiredService<IFileHashesService>());

    private async Task BackUpOriginal(Loadout.ReadOnly loadout)
    {
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(GameBsa, new MemoryStream(GameBsaBytes)), FileIdentity.XxHash3(GameBsaBytes.xxHash3()), Size.FromLong(GameBsaBytes.Length))]);
        using var tx = Connection.BeginTransaction();
        _ = new GameBackedUpFile.New(tx) { Hash = GameBsaBytes.xxHash3(), GameInstallId = loadout.InstallationId };
        await tx.Commit();
    }

    /// <summary>
    /// Makes the first recorded disk state look the way it did before identities carried their kind: every row at that
    /// transaction has only its xxHash3, and the install's initial state is that transaction.
    /// </summary>
    private async Task MakeInitialStateLegacy(Loadout.ReadOnly loadout, Action<ITransaction, DiskStateEntry.ReadOnly>? rewrite = null)
    {
        TxId legacy;
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var entry in DiskStateEntry.FindByGame(Connection.Db, loadout.InstallationId))
            {
                if (rewrite is not null) { rewrite(tx, entry); continue; }
                if (DiskStateEntry.Identity.TryGetValue(entry, out var identity)) tx.Retract(entry.Id, DiskStateEntry.Identity, identity);
            }
            legacy = (await tx.Commit()).NewTx;
        }

        using var point = Connection.BeginTransaction();
        point.Add(loadout.InstallationId, GameInstallMetadata.InitialDiskStateTransaction, EntityId.From(legacy.Value));
        await point.Commit();
    }

    private async Task ReplaceTheLiveFile(Loadout.ReadOnly loadout)
    {
        var changed = (byte[])GameBsaBytes.Clone();
        changed[0] ^= 0xFF;
        await loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, GameBsa)).WriteAllBytesAsync(changed);
    }

    [Fact]
    public async Task InitialState_ReadBeforeTheMigration_StillNamesEveryGameFile()
    {
        var loadout = await CreateLoadout();
        await BackUpOriginal(loadout);
        await MakeInitialStateLegacy(loadout);
        await ReplaceTheLiveFile(loadout);

        var (stream, hash) = await Lookup(Refresh(loadout)).Open(GameBsa);

        hash.Should().Be(FileIdentity.XxHash3(GameBsaBytes.xxHash3()), "the backup of the original is found through the legacy row");
        using var read = new MemoryStream();
        await using (stream) await stream.CopyToAsync(read);
        read.ToArray().Should().Equal(GameBsaBytes);
    }

    [Fact]
    public async Task InitialState_RowOfAnotherKind_IsJoinedToTheArchivedBytesByARelation()
    {
        var loadout = await CreateLoadout();
        await BackUpOriginal(loadout);
        var hashes = await MultiHasher.HashStream(new MemoryStream(GameBsaBytes));
        var local = ServiceProvider.GetRequiredService<IFileHashesService>().Local.Connection;
        using (var tx = local.BeginTransaction())
        {
            _ = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = hashes.XxHash64,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            await tx.Commit();
        }
        await MakeInitialStateLegacy(loadout, (tx, entry) =>
        {
            var current = entry.IdentityOf();
            if (DiskStateEntry.XxHash3.TryGetValue(entry, out var xx)) tx.Retract(entry.Id, DiskStateEntry.XxHash3, xx);
            tx.Add(entry.Id, DiskStateEntry.Identity, ((GamePath)entry.Path).Path == GameBsa ? FileIdentity.Sha1(hashes.Sha1) : current);
            if (current.TryXxHash3(out var kept) && ((GamePath)entry.Path).Path != GameBsa) tx.Add(entry.Id, DiskStateEntry.XxHash3, kept);
        });
        await ReplaceTheLiveFile(loadout);

        var (stream, hash) = await Lookup(Refresh(loadout)).Open(GameBsa);

        hash.Should().Be(FileIdentity.XxHash3(GameBsaBytes.xxHash3()));
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task KnownXxHash64_ComesFromTheRecordedRelation()
    {
        var loadout = await CreateLoadout();
        await BackUpOriginal(loadout);
        await ReplaceTheLiveFile(loadout);
        var hashes = await MultiHasher.HashStream(new MemoryStream(GameBsaBytes));
        // A digest no read of the bytes would give: only the recorded relation can be the source of it.
        var recorded = Hash.From(hashes.XxHash64.Value ^ 0xFFUL);
        var local = ServiceProvider.GetRequiredService<IFileHashesService>().Local.Connection;
        using (var tx = local.BeginTransaction())
        {
            _ = new HashRelation.New(tx)
            {
                MinimalHash = hashes.MinimalHash,
                XxHash3 = hashes.XxHash3,
                Sha1 = hashes.Sha1,
                XxHash64 = recorded,
                Md5 = hashes.Md5,
                Size = hashes.Size,
                Crc32 = hashes.Crc32,
            };
            await tx.Commit();
        }

        var described = await Lookup(Refresh(loadout)).Describe(GameBsa);

        described.Should().NotBeNull();
        described!.Value.XxHash3.Should().Be(FileIdentity.XxHash3(hashes.XxHash3));
        described.Value.XxHash64.Should().Be(FileIdentity.XxHash64(recorded));
    }

    private async Task RecordRelation(byte[] bytes, Func<MultiHash, Hash> xxHash64)
    {
        var hashes = await MultiHasher.HashStream(new MemoryStream(bytes));
        var local = ServiceProvider.GetRequiredService<IFileHashesService>().Local.Connection;
        using var tx = local.BeginTransaction();
        _ = new HashRelation.New(tx)
        {
            MinimalHash = hashes.MinimalHash,
            XxHash3 = hashes.XxHash3,
            Sha1 = hashes.Sha1,
            XxHash64 = xxHash64(hashes),
            Md5 = hashes.Md5,
            Size = hashes.Size,
            Crc32 = hashes.Crc32,
        };
        await tx.Commit();
    }

    [Fact]
    public async Task LiveFileTheIndexKnows_IsNotReadWhenARelationRecordsItsXxHash64()
    {
        var loadout = await CreateLoadout();
        // A digest no read of the bytes would give: only the recorded relation can be the source of it.
        var recorded = Hash.From((await MultiHasher.HashStream(new MemoryStream(GameBsaBytes))).XxHash64.Value ^ 0xFFUL);
        await RecordRelation(GameBsaBytes, _ => recorded);
        long read = 0;

        var described = await Lookup(Refresh(loadout)).Describe(GameBsa, onRead: n => read += n);

        read.Should().Be(0, "the disk state already names the file, and the relation names its other digest");
        described.Should().NotBeNull();
        described!.Value.XxHash64.Should().Be(FileIdentity.XxHash64(recorded));
    }

    private GameFileSourceLookup Unmanaged(Loadout.ReadOnly loadout, Action<AbsolutePath>? onOpen = null)
        => new(loadout.InstallationInstance, null, Connection, FileStore,
            ServiceProvider.GetRequiredService<SilverModManager.Abstractions.Games.FileHashes.IFileIdentityResolver>(),
            ServiceProvider.GetRequiredService<IFileHashesService>())
        {
            OpenLive = file => { onOpen?.Invoke(file); return SequentialFileRead.Open(file); },
        };

    [Fact]
    public async Task ARead_LeavesRowsThatLetTheNextLookupSkipIt()
    {
        var loadout = Refresh(await CreateLoadout());
        long first = 0, second = 0;

        var before = await Lookup(loadout).Describe(GameBsa, onRead: n => first += n);
        var after = await Lookup(loadout).Describe(GameBsa, onRead: n => second += n);

        first.Should().BeGreaterThan(0, "nothing recorded the file's xxHash64 yet");
        second.Should().Be(0, "the first read left a relation behind");
        after.Should().Be(before);
        after!.Value.XxHash64.Should().Be(Hash64(GameBsaBytes));
    }

    [Fact]
    public async Task ARead_OfAnInstallTheAppDoesNotManage_IsRememberedByTheFileFingerprint()
    {
        var loadout = Refresh(await CreateLoadout());
        long first = 0, second = 0;

        await Unmanaged(loadout).Describe(GameBsa, onRead: n => first += n);
        var after = await Unmanaged(loadout).Describe(GameBsa, onRead: n => second += n);

        first.Should().BeGreaterThan(0);
        second.Should().Be(0, "no disk state names the file, so only the fingerprint row can");
        after!.Value.XxHash64.Should().Be(Hash64(GameBsaBytes));
    }

    [Fact]
    public async Task AChangedFile_IsReadAgainWhateverWasRecordedForItsOldBytes()
    {
        var loadout = Refresh(await CreateLoadout());
        await Unmanaged(loadout).Describe(GameBsa);
        var changed = GameBsaBytes.Append((byte)1).ToArray();
        await loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LocationId.Game, GameBsa)).WriteAllBytesAsync(changed);
        long read = 0;

        var described = await Unmanaged(loadout).Describe(GameBsa, onRead: n => read += n);

        read.Should().BeGreaterThan(0, "the size and modified time no longer match any row");
        described!.Value.XxHash64.Should().Be(Hash64(changed));
    }

    [Fact]
    public async Task ALiveFileThatIsTheBackedUpOriginal_IsReadInsteadOfTheBackup()
    {
        var loadout = await CreateLoadout();
        await BackUpOriginal(loadout);
        var opened = new List<AbsolutePath>();
        var lookup = new GameFileSourceLookup(Refresh(loadout), Connection, FileStore,
            ServiceProvider.GetRequiredService<SilverModManager.Abstractions.Games.FileHashes.IFileIdentityResolver>(),
            ServiceProvider.GetRequiredService<IFileHashesService>())
        {
            OpenLive = file => { opened.Add(file); return SequentialFileRead.Open(file); },
        };

        var described = await lookup.Describe(GameBsa);

        opened.Should().ContainSingle("the unchanged live file is the original, and unpacking the backup is the slow way to the same bytes");
        described!.Value.XxHash64.Should().Be(Hash64(GameBsaBytes));
    }

    [Fact]
    public async Task ABackedUpOriginalWithARecordedRelation_IsNeverUnpacked()
    {
        var loadout = await CreateLoadout();
        await BackUpOriginal(loadout);
        await ReplaceTheLiveFile(loadout);
        var recorded = Hash.From((await MultiHasher.HashStream(new MemoryStream(GameBsaBytes))).XxHash64.Value ^ 0xFFUL);
        await RecordRelation(GameBsaBytes, _ => recorded);
        long read = 0;

        var described = await Lookup(Refresh(loadout)).Describe(GameBsa, onRead: n => read += n);

        read.Should().Be(0);
        described!.Value.XxHash64.Should().Be(FileIdentity.XxHash64(recorded));
    }

    [Fact]
    public async Task ABackedUpOriginalThatMustBeRead_LeavesARelationForTheNextLookup()
    {
        var loadout = await CreateLoadout();
        await BackUpOriginal(loadout);
        await ReplaceTheLiveFile(loadout);
        long first = 0, second = 0;

        await Lookup(Refresh(loadout)).Describe(GameBsa, onRead: n => first += n);
        var after = await Lookup(Refresh(loadout)).Describe(GameBsa, onRead: n => second += n);

        first.Should().BeGreaterThan(0);
        second.Should().Be(0);
        after!.Value.XxHash64.Should().Be(Hash64(GameBsaBytes));
    }
}
