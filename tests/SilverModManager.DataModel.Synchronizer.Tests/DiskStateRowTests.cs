using SilverModManager.Sdk.Hashes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3.Paths;
using SilverModManager.Backend.Games;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Rules;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Query;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// The disk state rows an apply writes and removes: they belong to the install's one row, and removing one
/// removes what is stored, not what the sync tree remembered.
/// </summary>
public class DiskStateRowTests(ITestOutputHelper helper) : AStubbedGameTest<DiskStateRowTests>(helper)
{
    private static readonly GamePath Plain = new(LocationId.Game, "plain.txt");

    private readonly FailingHasher _hasher = new();

    protected override IServiceCollection AddServices(IServiceCollection services)
        => base.AddServices(services).AddSingleton<IFullFileHasher>(_hasher);

    private const int FilesInThreeBatches = 2500;

    private void WriteGameFiles(int count)
    {
        var folder = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "bulk"));
        folder.CreateDirectory();
        for (var i = 0; i < count; i++) File.WriteAllText(folder.Combine($"file-{i:D5}.txt").ToString(), $"contents {i}");
    }

    private GameInstallMetadata.ReadOnly CurrentMetadata()
        => GameInstallMetadata.Load(Connection.Db, GameRegistry.ForceGetMetadata(GameInstallation).Id);

    [Fact]
    public async Task HasherFailingMidway_LeavesTheFinishedBatchesCommittedAndTheRerunHashesOnlyTheRest()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        WriteGameFiles(FilesInThreeBatches);
        _hasher.FailAfter(1500);

        var act = () => Synchronizer.RescanFiles(GameInstallation);

        (await act.Should().ThrowAsync<GameFolderUnreadableException>()).Which.InnerException.Should().BeOfType<IOException>();
        DiskStateEntry.FindByGame(Connection.Db, CurrentMetadata()).Should().HaveCount(1000);
        CurrentMetadata().Contains(GameInstallMetadata.InitialDiskStateTransaction).Should().BeFalse();

        _hasher.Reset();
        await Synchronizer.RescanFiles(GameInstallation);

        _hasher.Calls.Should().Be(1500);
        DiskStateEntry.FindByGame(Connection.Db, CurrentMetadata()).Should().HaveCount(FilesInThreeBatches);
        CurrentMetadata().Contains(GameInstallMetadata.InitialDiskStateTransaction).Should().BeTrue();
    }

    [Fact]
    public async Task CancelledReindex_StopsBeforeTheNextBatch()
    {
        await LoadoutManager.ManageInstallation(GameInstallation);
        WriteGameFiles(FilesInThreeBatches);
        using var cts = new CancellationTokenSource();
        _hasher.OnCall = call => { if (call == 1001) cts.Cancel(); };

        var act = () => Synchronizer.RescanFiles(GameInstallation, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        DiskStateEntry.FindByGame(Connection.Db, CurrentMetadata()).Should().HaveCount(1000);
        CurrentMetadata().Contains(GameInstallMetadata.InitialDiskStateTransaction).Should().BeFalse();
    }

    /// <summary>
    /// Hashes like the real one until told to fail. It ignores the cancellation token on purpose, so a file that
    /// has started is finished and the batch contents do not depend on how the readers interleave.
    /// </summary>
    private sealed class FailingHasher : IFullFileHasher
    {
        private int _calls;
        private int _failAfter = int.MaxValue;
        public int Calls => Volatile.Read(ref _calls);
        public Action<int>? OnCall { get; set; }
        public void FailAfter(int calls) => _failAfter = calls;
        public void Reset() { _calls = 0; _failAfter = int.MaxValue; OnCall = null; }

        public async ValueTask<Hash> Hash(AbsolutePath file, CancellationToken cancellationToken)
        {
            Enter(file);
            return await file.XxHash3Async(token: CancellationToken.None);
        }

        public async ValueTask<MultiHash> HashAll(AbsolutePath file, CancellationToken cancellationToken)
        {
            Enter(file);
            await using var stream = file.Read();
            return await MultiHasher.HashStream(stream, CancellationToken.None);
        }

        private void Enter(AbsolutePath file)
        {
            var call = Interlocked.Increment(ref _calls);
            OnCall?.Invoke(call);
            if (call > _failAfter) throw new IOException($"Input/output error reading {file}", 5);
        }
    }

    [Fact]
    public async Task DeletingAFileWhoseEntryChangedAfterTheTreeWasBuilt_RemovesTheWholeEntry()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"plain.txt"], loadout.LoadoutId, "mod");
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var mod in LoadoutItem.FindByLoadout(Connection.Db, loadout).Where(item => item.Name == "mod"))
                tx.Delete(mod.Id, recursive: true);
            await tx.Commit();
        }

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree[Plain].Actions.Should().HaveFlag(Actions.DeleteFromDisk);
        var entry = tree[Plain].Disk.EntityId;

        // A rescan that lands between building the tree and applying it moves the entry past what the tree saw.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(entry, DiskStateEntry.Identity, FileIdentity.XxHash3(Hash.From(0xC4A6E)));
            tx.Add(entry, DiskStateEntry.XxHash3, Hash.From(0xC4A6E));
            await tx.Commit();
        }

        await Synchronizer.RunActions(tree, Refresh(loadout));

        Connection.Db.Get(entry).Should().BeEmpty("a file that is gone from disk keeps no disk state");
    }

    // An earlier build could leave an entry with its path but not its hash, size or time. Reading the disk
    // state must not lend it the next entry's values.
    [Fact]
    public async Task ReadingDiskState_AnEntryMissingItsHash_IsLeftOutAndTheNextEntryKeepsItsOwn()
    {
        EntityId row, whole;
        using (var tx = Connection.BeginTransaction())
        {
            var r = new GameInstallMetadata.New(tx)
            {
                Store = GameStore.Steam,
                Path = "/not-a-folder/half-written",
                Game = GameId.From("HalfWritten"),
                Name = "Half Written",
            };
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = Connection.BeginTransaction())
        {
            var half = tx.TempId(DiskStateEntry.EntryPartition);
            tx.Add(half, DiskStateEntry.Path, new GamePath(LocationId.Game, "half.txt").ToGamePathParentTuple(row));
            tx.Add(half, DiskStateEntry.Game, row);
            var w = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
            {
                Path = new GamePath(LocationId.Game, "whole.txt").ToGamePathParentTuple(row),
                Identity = FileIdentity.XxHash3(Hash.From(0x2)),
                XxHash3 = Hash.From(0x2),
                Size = Size.From(2),
                LastModified = DateTimeOffset.UnixEpoch,
                GameId = row,
            };
            var result = await tx.Commit();
            whole = result.Remap(w).Id;
            result[half].Value.Should().BeLessThan(whole.Value, "the half entry has to come first in the merge join");
        }

        var pairs = Synchronizer.GetDiskStateForGame(GameInstallMetadata.Load(Connection.Db, row));

        pairs.Should().ContainSingle();
        pairs[0].Part.EntityId.Should().Be(whole);
        pairs[0].Path.Should().Be(new GamePath(LocationId.Game, "whole.txt"));
        pairs[0].Part.Identity.Should().Be(FileIdentity.XxHash3(Hash.From(0x2)));
    }

    private async Task<EntityId> NewInstall()
    {
        using var tx = Connection.BeginTransaction();
        var r = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Steam,
            Path = "/not-a-folder/typed-" + Guid.NewGuid(),
            Game = GameId.From("Typed"),
            Name = "Typed",
        };
        var result = await tx.Commit();
        return result.Remap(r).Id;
    }

    private static EntityId AddEntry(ITransaction tx, EntityId row, string path, Action<ITransaction, EntityId> identityDatoms)
    {
        var id = tx.TempId(DiskStateEntry.EntryPartition);
        tx.Add(id, DiskStateEntry.Path, new GamePath(LocationId.Game, path).ToGamePathParentTuple(row));
        tx.Add(id, DiskStateEntry.Game, row);
        tx.Add(id, DiskStateEntry.Size, Size.From(3));
        tx.Add(id, DiskStateEntry.LastModified, DateTimeOffset.UnixEpoch);
        identityDatoms(tx, id);
        return id;
    }

    [Fact]
    public async Task ReadingDiskState_ARowWrittenBeforeIdentitiesCarriedTheirKind_IsReadAsAnXxHash3()
    {
        var row = await NewInstall();
        using (var tx = Connection.BeginTransaction())
        {
            AddEntry(tx, row, "legacy.txt", (t, id) => t.Add(id, DiskStateEntry.XxHash3, Hash.From(0xC4A6E)));
            await tx.Commit();
        }

        var pairs = Synchronizer.GetDiskStateForGame(GameInstallMetadata.Load(Connection.Db, row));

        pairs.Should().ContainSingle(p => p.Part.Identity == FileIdentity.XxHash3(Hash.From(0xC4A6E)));
    }

    [Fact]
    public async Task ReadingDiskState_AMigratedRow_IsReadOnce()
    {
        var row = await NewInstall();
        using (var tx = Connection.BeginTransaction())
        {
            AddEntry(tx, row, "migrated.txt", (t, id) =>
            {
                t.Add(id, DiskStateEntry.XxHash3, Hash.From(0x77));
                t.Add(id, DiskStateEntry.Identity, FileIdentity.XxHash3(Hash.From(0x77)));
            });
            await tx.Commit();
        }

        var pairs = Synchronizer.GetDiskStateForGame(GameInstallMetadata.Load(Connection.Db, row));

        pairs.Should().ContainSingle().Which.Part.Identity.Should().Be(FileIdentity.XxHash3(Hash.From(0x77)));
    }

    [Fact]
    public async Task ReadingDiskState_ARowOfAnotherKindWithoutAnXxHash3_IsReadWithItsKindAndItsNeighboursKeepTheirOwn()
    {
        var row = await NewInstall();
        var sha1 = FileIdentity.Sha1(Sha1Value.FromHex("0123456789ABCDEF0123456789ABCDEF01234567"));
        using (var tx = Connection.BeginTransaction())
        {
            // The kinds alternate so a merge that took one iterator's value for its neighbour's entry would show.
            AddEntry(tx, row, "a.txt", (t, id) => t.Add(id, DiskStateEntry.XxHash3, Hash.From(0xA)));
            AddEntry(tx, row, "b.txt", (t, id) => t.Add(id, DiskStateEntry.Identity, sha1));
            AddEntry(tx, row, "c.txt", (t, id) =>
            {
                t.Add(id, DiskStateEntry.XxHash3, Hash.From(0xC));
                t.Add(id, DiskStateEntry.Identity, FileIdentity.XxHash3(Hash.From(0xC)));
            });
            await tx.Commit();
        }

        var pairs = Synchronizer.GetDiskStateForGame(GameInstallMetadata.Load(Connection.Db, row)).ToDictionary(p => p.Path.Path.ToString(), p => p.Part.Identity);

        pairs.Should().BeEquivalentTo(new Dictionary<string, FileIdentity>
        {
            ["a.txt"] = FileIdentity.XxHash3(Hash.From(0xA)),
            ["b.txt"] = sha1,
            ["c.txt"] = FileIdentity.XxHash3(Hash.From(0xC)),
        });
    }

    [Fact]
    public async Task ReadingDiskStateAsOfTheInitialTransaction_StillSeesRowsWrittenBeforeTheMigration()
    {
        var row = await NewInstall();
        TxId initial;
        using (var tx = Connection.BeginTransaction())
        {
            AddEntry(tx, row, "legacy.txt", (t, id) => t.Add(id, DiskStateEntry.XxHash3, Hash.From(0x51)));
            initial = (await tx.Commit()).NewTx;
        }

        // What the migration does to the rows that exist.
        using (var tx = Connection.BeginTransaction())
        {
            foreach (var entry in DiskStateEntry.FindByGame(Connection.Db, row))
                tx.Add(entry.Id, DiskStateEntry.Identity, FileIdentity.XxHash3(DiskStateEntry.XxHash3.Get(entry)));
            await tx.Commit();
        }

        var asOf = ((ILoadoutSynchronizer)Synchronizer).GetDiskStateForGameAsOf(GameInstallMetadata.Load(Connection.Db, row), initial);

        asOf.Should().ContainSingle(p => p.Part.Identity == FileIdentity.XxHash3(Hash.From(0x51)));
    }

    [Fact]
    public async Task WritingAnIdentity_KeepsTheXxHash3MirrorForAnXxHash3AndDropsItForAnotherKind()
    {
        var row = await NewInstall();
        EntityId entryId;
        using (var tx = Connection.BeginTransaction())
        {
            var e = AddEntry(tx, row, "flip.txt", (t, id) =>
            {
                t.Add(id, DiskStateEntry.XxHash3, Hash.From(0x1));
                t.Add(id, DiskStateEntry.Identity, FileIdentity.XxHash3(Hash.From(0x1)));
            });
            entryId = (await tx.Commit())[e];
        }

        var sha1 = FileIdentity.Sha1(Sha1Value.FromHex(new string('B', 40)));
        using (var tx = Connection.BeginTransaction())
        {
            ALoadoutSynchronizer.WriteIdentity(tx, DiskStateEntry.Load(Connection.Db, entryId), sha1);
            await tx.Commit();
        }

        var asSha1 = DiskStateEntry.Load(Connection.Db, entryId);
        asSha1.IdentityOf().Should().Be(sha1);
        DiskStateEntry.XxHash3.Contains(asSha1).Should().BeFalse("only an xxHash3 identity has the mirror");

        using (var tx = Connection.BeginTransaction())
        {
            ALoadoutSynchronizer.WriteIdentity(tx, asSha1, FileIdentity.XxHash3(Hash.From(0x9)));
            await tx.Commit();
        }

        var back = DiskStateEntry.Load(Connection.Db, entryId);
        back.IdentityOf().Should().Be(FileIdentity.XxHash3(Hash.From(0x9)));
        DiskStateEntry.XxHash3.Get(back).Should().Be(Hash.From(0x9));
    }

    [Fact]
    public async Task StoredIdentity_ComesBackWithItsKind_AndIsStoredAsKindThenDigest()
    {
        var row = await NewInstall();
        var sha1 = FileIdentity.Sha1(Sha1Value.FromHex("0123456789ABCDEF0123456789ABCDEF01234567"));
        var xxh3 = FileIdentity.XxHash3(Hash.From(0x0123456789ABCDEF));
        EntityId a, b;
        using (var tx = Connection.BeginTransaction())
        {
            var ea = AddEntry(tx, row, "a.txt", (t, id) => t.Add(id, DiskStateEntry.Identity, sha1));
            var eb = AddEntry(tx, row, "b.txt", (t, id) => t.Add(id, DiskStateEntry.Identity, xxh3));
            var result = await tx.Commit();
            (a, b) = (result[ea], result[eb]);
        }

        DiskStateEntry.Identity.Get(DiskStateEntry.Load(Connection.Db, a)).Should().Be(sha1);
        DiskStateEntry.Identity.Get(DiskStateEntry.Load(Connection.Db, b)).Should().Be(xxh3);

        var db = Connection.Db;
        var raw = new Dictionary<EntityId, byte[]>();
        using (var iterator = db.LightweightDatoms(SliceDescriptor.Create(db.AttributeCache.GetAttributeId(DiskStateEntry.Identity.Id))))
        {
            while (iterator.MoveNext()) raw[iterator.KeyPrefix.E] = iterator.ValueSpan.ToArray();
        }
        raw[a].Should().Equal([(byte)HashKind.Sha1, ..sha1.Digest.ToArray()]);
        raw[b].Should().Equal([(byte)HashKind.XxHash3, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF]);
    }

    [Fact]
    public async Task IndexingTheFolder_WritesAnIdentityBesideTheXxHash3OfEveryRow()
    {
        var loadout = await CreateLoadout();
        var file = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, "indexed.txt"));
        file.Parent.CreateDirectory();
        await file.WriteAllTextAsync("indexed");
        await Synchronizer.ReindexState(GameInstallation);

        var rows = DiskStateEntry.FindByGame(Connection.Db, loadout.Installation).ToArray();

        rows.Should().NotBeEmpty();
        rows.Should().OnlyContain(r => DiskStateEntry.Identity.Contains(r) && DiskStateEntry.XxHash3.Contains(r));
        rows.Should().OnlyContain(r => r.IdentityOf() == FileIdentity.XxHash3(DiskStateEntry.XxHash3.Get(r)));
    }

    // The second row reaches the same folder through a link, so its path is spelled differently and the refusal
    // has to show both for the player to see they are one folder.
    [Fact]
    public async Task ApplyingALoadoutOnASecondRowForTheSameFolder_NamesBothRowsPaths()
    {
        var first = await CreateLoadout();
        var canonical = first.Installation;
        var alias = TestScratch.Combine($"second-spelling-{Guid.NewGuid():N}");
        Directory.CreateSymbolicLink(alias, canonical.Path);
        try
        {
            EntityId stray;
            using (var tx = Connection.BeginTransaction())
            {
                var row = new GameInstallMetadata.New(tx)
                {
                    Store = canonical.Store,
                    Path = alias,
                    Game = canonical.Game.Value,
                    Name = canonical.Name,
                };
                var loadout = new Loadout.New(tx)
                {
                    Name = "Stray",
                    ShortName = "s",
                    InstallationId = row,
                    LoadoutKind = LoadoutKind.Default,
                    Revision = 0,
                    GameVersion = VanityVersion.From("Unknown"),
                };
                var result = await tx.Commit();
                stray = result.Remap(loadout).Id;
            }

            var act = async () => await Synchronizer.Synchronize(Loadout.Load(Connection.Db, stray));

            var refusal = (await act.Should().ThrowAsync<SynchronizerPreflightException>()).Which.Message;
            refusal.Should().Contain($"`{alias}`").And.Contain($"`{canonical.Path}`");
        }
        finally
        {
            File.Delete(alias);
        }
    }

    [Fact]
    public async Task ApplyingALoadoutOnASecondRowForTheSameFolder_IsRefusedBeforeAnythingIsWritten()
    {
        var first = await CreateLoadout();
        var canonical = first.Installation;

        EntityId duplicate, stray;
        using (var tx = Connection.BeginTransaction())
        {
            var row = new GameInstallMetadata.New(tx)
            {
                Store = canonical.Store,
                Path = canonical.Path,
                Game = canonical.Game.Value,
                Name = canonical.Name,
            };
            var loadout = new Loadout.New(tx)
            {
                Name = "Stray",
                ShortName = "s",
                InstallationId = row,
                LoadoutKind = LoadoutKind.Default,
                Revision = 0,
                GameVersion = VanityVersion.From("Unknown"),
            };
            var result = await tx.Commit();
            (duplicate, stray) = (result.Remap(row).Id, result.Remap(loadout).Id);
        }
        var lastSyncedBefore = GameInstallMetadata.Load(Connection.Db, canonical.Id).LastSyncedLoadout.Id;

        var act = async () => await Synchronizer.Synchronize(Loadout.Load(Connection.Db, stray));

        await act.Should().ThrowAsync<SynchronizerPreflightException>();
        DiskStateEntry.FindByGame(Connection.Db, duplicate).Should().BeEmpty();
        GameInstallMetadata.LastSyncedLoadout.Contains(GameInstallMetadata.Load(Connection.Db, duplicate)).Should().BeFalse();
        GameInstallMetadata.Load(Connection.Db, canonical.Id).LastSyncedLoadout.Id.Should().Be(lastSyncedBefore);
    }
}
