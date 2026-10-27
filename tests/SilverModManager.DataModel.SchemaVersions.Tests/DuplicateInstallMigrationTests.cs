using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
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
        kept.ShortName.Should().Be("a", "the older row's loadouts keep their names");
        kept.Name.Should().Be("Loadout A");

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
            var cd = NewDiskEntry(tx, canonical);
            NewPlanEntry(tx, canonical, LocationId.Game, "copy");
            tx.Add(canonical, GameInstallMetadata.LastSyncedLoadout, canonicalLoadout);
            tx.Add(canonical, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
            var result = await tx.Commit();
            canonicalDisk = result.Remap(cd).Id;
        }

        using (var tx = connection.BeginTransaction())
        {
            var dd = NewDiskEntry(tx, duplicate);
            NewPlanEntry(tx, duplicate, LocationId.Game, "hardlink");
            NewPlanEntry(tx, duplicate, LocationId.Saves, "copy");
            var b = new GameBackedUpFile.New(tx) { Hash = Hash.From(0xB4C), GameInstallId = duplicate };
            tx.Add(duplicate, GameInstallMetadata.LastSyncedLoadout, duplicateLoadout);
            tx.Add(duplicate, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
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

    private static DiskStateEntry.New NewDiskEntry(ITransaction tx, EntityId installation) => new(tx, tx.TempId(DiskStateEntry.EntryPartition))
    {
        Path = new GamePath(LocationId.Game, "vanilla/x.txt").ToGamePathParentTuple(installation),
        Hash = Hash.From(0x1),
        Size = Size.From(1),
        LastModified = DateTimeOffset.UnixEpoch,
        GameId = installation,
    };

    private static void NewPlanEntry(ITransaction tx, EntityId installation, LocationId location, string backend)
        => _ = new DeploymentPlanEntry.New(tx)
        {
            InstallationId = installation,
            Root = new GamePath(location, "").ToGamePathParentTuple(installation),
            Backend = backend,
        };
}
