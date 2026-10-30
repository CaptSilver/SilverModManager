using SilverModManager.Sdk.Hashes;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Deployment;
using SilverModManager.Deployment.Vfs;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Adding a game twice at once used to give its folder one installation row per call, each with its own loadouts,
/// disk state and deployment plan. A sync then read one row's disk state and wrote another's.
/// </summary>
public class DuplicateInstallMigrationTests
{
    private const string Folder = "/games/stubbed";
    private static readonly GameId Game = GameId.From("StubbedGame");

    [Fact]
    public async Task MigrateAll_TwoRowsForOneFolder_LeavesTheOlderRowWithEverythingOnIt()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var seed = await Seed(connection);

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        GameInstallMetadata.Load(db, seed.Duplicate).IsValid().Should().BeFalse("the duplicate row is gone");
        db.ReferencesTo(seed.Duplicate).Should().BeEmpty("nothing may point at a row that no longer exists");
        GameInstallMetadata.FindByGame(db, Game).Select(row => row.Id).Should().BeEquivalentTo([seed.Canonical, seed.OtherFolder]);

        var moved = Loadout.Load(db, seed.DuplicateLoadout);
        moved.InstallationId.Value.Should().Be(seed.Canonical);
        var kept = Loadout.Load(db, seed.CanonicalLoadout);
        moved.ShortName.Should().NotBe(kept.ShortName, "loadouts of one install are told apart by short name");
        moved.Name.Should().NotBeEquivalentTo(kept.Name);
        moved.ShortName.Should().Be("a", "the loadout on disk keeps its names, which is what launching by name finds");
        moved.Name.Should().Be("Loadout A");

        var canonical = GameInstallMetadata.Load(db, seed.Canonical);
        canonical.LastSyncedLoadout.Id.Should().Be(seed.DuplicateLoadout, "the duplicate row was applied last");

        DiskStateEntry.FindByGame(db, seed.Canonical).Select(e => e.Id).Should().Equal([seed.CanonicalDiskEntry], "the older row is the one every rescan wrote");
        DiskStateEntry.Load(db, seed.DuplicateDiskEntry).IsValid().Should().BeFalse();

        var plan = DeploymentPlanEntry.FindByInstallation(db, seed.Canonical).ToArray();
        plan.Should().HaveCount(2);
        plan.Should().OnlyContain(entry => entry.Root.Item1 == seed.Canonical);
        plan.Single(entry => entry.Root.Item2 == LocationId.Game).Backend.Should().Be("hardlink", "the root's backend is the one the last apply used");
        plan.Single(entry => entry.Root.Item2 == LocationId.Saves).Backend.Should().Be("copy", "a root only the duplicate planned moves over");

        GameBackedUpFile.Load(db, seed.DuplicateBackup).GameInstallId.Value.Should().Be(seed.Canonical);
        Loadout.Load(db, seed.OtherFolderLoadout).InstallationId.Value.Should().Be(seed.OtherFolder, "another folder's row is not a duplicate");
    }

    // Most databases never had a duplicate. The migration must then write its bookkeeping and nothing else.
    [Fact]
    public async Task MigrateAll_NoDuplicateRows_WritesOnlyTheSchemaVersionAndLeavesStagingAlone()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId row;
        using (var tx = connection.BeginTransaction())
        {
            var r = NewRow(tx, Folder);
            NewLoadout(tx, r);
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = connection.BeginTransaction())
        {
            NewDiskEntry(tx, row, "vanilla/x.txt");
            NewPlanEntry(tx, row, LocationId.Game, "hardlink");
            await tx.Commit();
        }
        var staged = StageFile(provider, row, Hash.From(0x5A), "staged");
        var stagingBefore = Snapshot(folder.StagingFolder);
        var before = connection.Db.BasisTxId;

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        var written = new List<string>();
        for (var tx = before.Value + 1; tx <= db.BasisTxId.Value; tx++)
        {
            var txEntity = EntityId.From(tx);
            written.AddRange(db.Datoms(TxId.From(tx)).Where(d => d.E != txEntity).Select(d => db.AttributeCache.GetSymbol(d.A).Namespace));
        }

        written.Should().NotBeEmpty().And.OnlyContain(ns => ns == SchemaVersion.Namespace || ns == "SilverModManager.DataModel.SchemaVersions.MigrationLogItem");
        Snapshot(folder.StagingFolder).Should().Equal(stagingBefore);
        File.ReadAllText(staged.ToString()).Should().Be("staged");
    }

    // Three rows, the middle one applied last, each holding some of the things that point at an installation.
    [Fact]
    public async Task MigrateAll_ThreeRowsTheOldestNotAppliedLast_PointsEverythingAtTheOldestAndKeepsTheLastApply()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();

        EntityId oldest, middle, youngest, oldestLoadout, middleLoadout, youngestLoadout;
        using (var tx = connection.BeginTransaction())
        {
            var (r1, r2, r3) = (NewRow(tx, Folder), NewRow(tx, Folder), NewRow(tx, Folder));
            var (l1, l2, l3) = (NewLoadout(tx, r1), NewLoadout(tx, r2), NewLoadout(tx, r3));
            var result = await tx.Commit();
            (oldest, middle, youngest) = (result.Remap(r1).Id, result.Remap(r2).Id, result.Remap(r3).Id);
            (oldestLoadout, middleLoadout, youngestLoadout) = (result.Remap(l1).Id, result.Remap(l2).Id, result.Remap(l3).Id);
        }

        EntityId tool, gameFiles, catalog, backup, youngestOnlyEntry, middleSharedEntry;
        using (var tx = connection.BeginTransaction())
        {
            NewDiskEntry(tx, oldest, "vanilla/x.txt");
            NewPlanEntry(tx, oldest, LocationId.Game, "copy");
            MarkApplied(tx, oldest, oldestLoadout);
            await tx.Commit();
        }
        using (var tx = connection.BeginTransaction())
        {
            var e = NewDiskEntry(tx, youngest, "mods/only-youngest.txt");
            NewPlanEntry(tx, youngest, LocationId.Game, "symlink");
            NewPlanEntry(tx, youngest, LocationId.Saves, "copy");
            var t = tx.TempId();
            tx.Add(t, UserTool.Installation, youngest);
            tx.Add(t, UserTool.Name, "Tool");
            var c = tx.TempId();
            tx.Add(c, VfsCatalogEntry.Installation, youngest);
            tx.Add(c, VfsCatalogEntry.Path, "/games/stubbed/vanilla/x.txt");
            MarkApplied(tx, youngest, youngestLoadout);
            var result = await tx.Commit();
            (youngestOnlyEntry, tool, catalog) = (result.Remap(e).Id, result[t], result[c]);
        }
        using (var tx = connection.BeginTransaction())
        {
            var e = NewDiskEntry(tx, middle, "vanilla/x.txt");
            NewPlanEntry(tx, middle, LocationId.Game, "hardlink");
            var g = tx.TempId();
            tx.Add(g, LoadoutGameFilesGroup.GameMetadata, middle);
            var b = new GameBackedUpFile.New(tx) { Hash = Hash.From(0xB4C), GameInstallId = middle };
            MarkApplied(tx, middle, middleLoadout);
            var result = await tx.Commit();
            (middleSharedEntry, gameFiles, backup) = (result.Remap(e).Id, result[g], result.Remap(b).Id);
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        GameInstallMetadata.FindByGame(db, Game).Select(row => row.Id).Should().Equal([oldest]);
        db.ReferencesTo(middle).Should().BeEmpty();
        db.ReferencesTo(youngest).Should().BeEmpty();

        RefOf(db, UserTool.Installation, tool).Should().Be(oldest);
        RefOf(db, VfsCatalogEntry.Installation, catalog).Should().Be(oldest);
        RefOf(db, LoadoutGameFilesGroup.GameMetadata, gameFiles).Should().Be(oldest);
        GameBackedUpFile.Load(db, backup).GameInstallId.Value.Should().Be(oldest);
        Loadout.FindByInstallation(db, oldest).Select(l => l.Id).Should().BeEquivalentTo([oldestLoadout, middleLoadout, youngestLoadout]);

        var canonical = GameInstallMetadata.Load(db, oldest);
        canonical.LastSyncedLoadout.Id.Should().Be(middleLoadout, "the middle row was applied last");

        var plan = DeploymentPlanEntry.FindByInstallation(db, oldest).ToArray();
        plan.Single(entry => entry.Root.Item2 == LocationId.Game).Backend.Should().Be("hardlink");
        plan.Single(entry => entry.Root.Item2 == LocationId.Saves).Backend.Should().Be("copy");

        var entries = DiskStateEntry.FindByGame(db, oldest).ToArray();
        entries.Select(e => e.Path.Item3.ToString()).Should().BeEquivalentTo(["vanilla/x.txt", "mods/only-youngest.txt"]);
        DiskStateEntry.Load(db, middleSharedEntry).IsValid().Should().BeFalse("the oldest row already tracks that path");
        var carried = DiskStateEntry.Load(db, youngestOnlyEntry);
        carried.Path.Item1.Should().Be(oldest, "a file only the youngest row deployed must stay tracked, or a protected path is never re-indexed");
        carried.Game.Id.Should().Be(oldest);

        var loadouts = new[] { oldestLoadout, middleLoadout, youngestLoadout }.Select(id => Loadout.Load(db, id)).ToArray();
        loadouts[1].ShortName.Should().Be("a", "the loadout on disk keeps its short name");
        loadouts[1].Name.Should().Be("Loadout A");
        loadouts.Select(l => l.ShortName).Should().OnlyHaveUniqueItems();
        loadouts.Select(l => l.Name).Should().OnlyHaveUniqueItems();
    }

    // A link apply of a loadout on a younger row staged under that row's id. Once the row is gone the sweep would
    // delete the directory, and every symlink deployed from it would dangle.
    [Fact]
    public async Task MigrateAll_DuplicateRowWithStagedFiles_HandsThemToTheOldestRow()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var seed = await Seed(connection);
        var hash = Hash.From(0x77);
        var staged = StageFile(provider, seed.Duplicate, hash, "deployed by the duplicate");
        var game = folder.StagingFolder.Parent.Combine("game");
        game.CreateDirectory();
        var link = game.Combine("linked.txt");
        File.CreateSymbolicLink(link.ToString(), staged.ToString());

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var store = provider.GetRequiredService<ILooseStagingStore>();
        store.Sweep(() => new Dictionary<GameInstallMetadataId, IReadOnlySet<FileIdentity>>
        {
            [GameInstallMetadataId.From(seed.Canonical)] = new HashSet<FileIdentity> { FileIdentity.XxHash3(hash) },
        });
        store.Contains(GameInstallMetadataId.From(seed.Canonical), FileIdentity.XxHash3(hash)).Should().BeTrue();
        store.Installations().Should().Equal([GameInstallMetadataId.From(seed.Canonical)]);
        File.ReadAllText(link.ToString()).Should().Be("deployed by the duplicate");
    }

    // Older Release builds could retract an entry's hash, size and time but not its path, when the path was
    // retracted with a value it no longer held. Reading such an entry throws.
    [Fact]
    public async Task MigrateAll_HalfRetractedDiskStateEntry_IsDeleted()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId row, whole, half;
        using (var tx = connection.BeginTransaction())
        {
            var r = NewRow(tx, Folder);
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = connection.BeginTransaction())
        {
            var w = NewDiskEntry(tx, row, "vanilla/x.txt");
            var h = NewHalfEntry(tx, row, "vanilla/y.txt");
            var result = await tx.Commit();
            (whole, half) = (result.Remap(w).Id, result[h]);
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        db.Get(half).Should().BeEmpty("nothing of the half-written entry may be left");
        DiskStateEntry.FindByGame(db, row).Select(e => e.Id).Should().Equal([whole]);
    }

    // A row of another hash kind has its identity but no xxHash3. It is complete, not half-written.
    [Fact]
    public async Task MigrateAll_DiskStateEntryWithOnlyAnIdentity_IsKept()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId row, sha1Entry;
        using (var tx = connection.BeginTransaction())
        {
            var r = NewRow(tx, Folder);
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = connection.BeginTransaction())
        {
            var entry = tx.TempId(DiskStateEntry.EntryPartition);
            tx.Add(entry, DiskStateEntry.Path, new GamePath(LocationId.Game, "vanilla/sha1.txt").ToGamePathParentTuple(row));
            tx.Add(entry, DiskStateEntry.Game, row);
            tx.Add(entry, DiskStateEntry.Identity, FileIdentity.Sha1(Sha1Value.FromHex(new string('A', 40))));
            tx.Add(entry, DiskStateEntry.Size, Size.From(1));
            tx.Add(entry, DiskStateEntry.LastModified, DateTimeOffset.UnixEpoch);
            var result = await tx.Commit();
            sha1Entry = result[entry];
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        DiskStateEntry.FindByGame(db, row).Select(e => e.Id).Should().Equal([sha1Entry]);
        var kept = DiskStateEntry.Load(db, sha1Entry);
        kept.IdentityOf().Kind.Should().Be(HashKind.Sha1);
        DiskStateEntry.XxHash3.Contains(kept).Should().BeFalse("only an xxHash3 identity has the mirror");
    }

    // The half-written entry is what a younger row's sync left of the oldest row's entry for a file it replaced.
    [Fact]
    public async Task MigrateAll_TheOldestRowsEntryIsHalfWritten_TheDuplicatesEntryForThatPathTakesItsPlace()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId canonical, duplicate, half, replacement;
        using (var tx = connection.BeginTransaction())
        {
            var (c, d) = (NewRow(tx, Folder), NewRow(tx, Folder));
            var result = await tx.Commit();
            (canonical, duplicate) = (result.Remap(c).Id, result.Remap(d).Id);
        }
        using (var tx = connection.BeginTransaction())
        {
            var h = NewHalfEntry(tx, canonical, "vanilla/x.txt");
            var r = NewDiskEntry(tx, duplicate, "vanilla/x.txt");
            var result = await tx.Commit();
            (half, replacement) = (result[h], result.Remap(r).Id);
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        db.Get(half).Should().BeEmpty();
        DiskStateEntry.FindByGame(db, canonical).Select(e => e.Id).Should().Equal([replacement]);
        DiskStateEntry.Load(db, replacement).Path.Item1.Should().Be(canonical);
    }

    private sealed record Seeded(
        EntityId Canonical,
        EntityId Duplicate,
        EntityId OtherFolder,
        EntityId CanonicalLoadout,
        EntityId DuplicateLoadout,
        EntityId OtherFolderLoadout,
        EntityId CanonicalDiskEntry,
        EntityId DuplicateDiskEntry,
        EntityId DuplicateBackup);

    private static async Task<Seeded> Seed(IConnection connection)
    {
        EntityId canonical, duplicate, other, canonicalLoadout, duplicateLoadout, otherLoadout;
        using (var tx = connection.BeginTransaction())
        {
            var c = NewRow(tx, Folder);
            var d = NewRow(tx, Folder);
            var o = NewRow(tx, "/games/stubbed-second");
            var cl = NewLoadout(tx, c);
            var dl = NewLoadout(tx, d);
            var ol = NewLoadout(tx, o);
            var result = await tx.Commit();
            (canonical, duplicate, other) = (result.Remap(c).Id, result.Remap(d).Id, result.Remap(o).Id);
            (canonicalLoadout, duplicateLoadout, otherLoadout) = (result.Remap(cl).Id, result.Remap(dl).Id, result.Remap(ol).Id);
        }

        canonical.Value.Should().BeLessThan(duplicate.Value, "the fixture needs the canonical row to be the older one");

        EntityId canonicalDisk, duplicateDisk, backup;
        using (var tx = connection.BeginTransaction())
        {
            var cd = NewDiskEntry(tx, canonical, "vanilla/x.txt");
            NewPlanEntry(tx, canonical, LocationId.Game, "copy");
            MarkApplied(tx, canonical, canonicalLoadout);
            var result = await tx.Commit();
            canonicalDisk = result.Remap(cd).Id;
        }

        using (var tx = connection.BeginTransaction())
        {
            var dd = NewDiskEntry(tx, duplicate, "vanilla/x.txt");
            NewPlanEntry(tx, duplicate, LocationId.Game, "hardlink");
            NewPlanEntry(tx, duplicate, LocationId.Saves, "copy");
            var b = new GameBackedUpFile.New(tx) { Hash = Hash.From(0xB4C), GameInstallId = duplicate };
            MarkApplied(tx, duplicate, duplicateLoadout);
            var result = await tx.Commit();
            (duplicateDisk, backup) = (result.Remap(dd).Id, result.Remap(b).Id);
        }

        return new Seeded(canonical, duplicate, other, canonicalLoadout, duplicateLoadout, otherLoadout, canonicalDisk, duplicateDisk, backup);
    }

    private static GameInstallMetadata.New NewRow(ITransaction tx, string path) => new(tx)
    {
        Store = GameStore.Steam,
        Path = path,
        Game = Game,
        Name = "Stubbed Game",
    };

    // Every concurrent CreateLoadout picked its short name from its own row, so each started at "a".
    private static Loadout.New NewLoadout(ITransaction tx, GameInstallMetadata.New installation) => new(tx)
    {
        Name = "Loadout A",
        ShortName = "a",
        InstallationId = installation,
        LoadoutKind = LoadoutKind.Default,
        Revision = 0,
        GameVersion = VanityVersion.From("Unknown"),
    };

    private static void MarkApplied(ITransaction tx, EntityId installation, EntityId loadout)
    {
        tx.Add(installation, GameInstallMetadata.LastSyncedLoadout, loadout);
        tx.Add(installation, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
    }

    private static DiskStateEntry.New NewDiskEntry(ITransaction tx, EntityId installation, string path) => new(tx, tx.TempId(DiskStateEntry.EntryPartition))
    {
        Path = new GamePath(LocationId.Game, path).ToGamePathParentTuple(installation),
        Identity = FileIdentity.XxHash3(Hash.From(0x1)),
        XxHash3 = Hash.From(0x1),
        Size = Size.From(1),
        LastModified = DateTimeOffset.UnixEpoch,
        GameId = installation,
    };

    private static EntityId NewHalfEntry(ITransaction tx, EntityId installation, string path)
    {
        var id = tx.TempId(DiskStateEntry.EntryPartition);
        tx.Add(id, DiskStateEntry.Path, new GamePath(LocationId.Game, path).ToGamePathParentTuple(installation));
        tx.Add(id, DiskStateEntry.Game, installation);
        return id;
    }

    private static void NewPlanEntry(ITransaction tx, EntityId installation, LocationId location, string backend)
        => _ = new DeploymentPlanEntry.New(tx)
        {
            InstallationId = installation,
            Root = new GamePath(location, "").ToGamePathParentTuple(installation),
            Backend = backend,
        };

    // Written by hand rather than through Ensure: the file store in this host has no archives.
    private static AbsolutePath StageFile(IServiceProvider provider, EntityId installation, Hash hash, string content)
    {
        var path = provider.GetRequiredService<ILooseStagingStore>().PathFor(GameInstallMetadataId.From(installation), FileIdentity.XxHash3(hash));
        path.Parent.CreateDirectory();
        File.WriteAllText(path.ToString(), content);
        return path;
    }

    private static EntityId RefOf(IDb db, ReferenceAttribute attribute, EntityId entity)
    {
        attribute.TryGetValue(db.Get(entity), out var value).Should().BeTrue($"{entity} holds {attribute.Id}");
        return value;
    }

    private static string[] Snapshot(AbsolutePath folder)
        => !folder.DirectoryExists() ? [] : Directory.EnumerateFileSystemEntries(folder.ToString(), "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
}
