using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// A disk state entry used to be a path, a size, a time and a bare xxHash3. It now also carries its identity.
/// </summary>
public class TypedDiskStateIdentityMigrationTests
{
    private static readonly GameId Game = GameId.From("StubbedGame");

    [Fact]
    public async Task Migration_GivesEveryLegacyRowAnIdentityAndKeepsItsHash()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId row, first, second;
        using (var tx = connection.BeginTransaction())
        {
            var r = new GameInstallMetadata.New(tx) { Store = GameStore.Steam, Path = "/games/typed", Game = Game, Name = "Typed" };
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        using (var tx = connection.BeginTransaction())
        {
            first = Legacy(tx, row, "a.txt", 0xA1);
            second = Legacy(tx, row, "b.txt", 0xB2);
            var result = await tx.Commit();
            (first, second) = (result[first], result[second]);
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var db = connection.Db;
        foreach (var (id, value) in new[] { (first, 0xA1UL), (second, 0xB2UL) })
        {
            var entry = DiskStateEntry.Load(db, id);
            DiskStateEntry.Identity.Get(entry).Should().Be(FileIdentity.XxHash3(Hash.From(value)));
            DiskStateEntry.XxHash3.Get(entry).Should().Be(Hash.From(value), "the xxHash3 attribute keeps its value");
        }
    }

    [Fact]
    public async Task Migration_ReadAsOfBeforeItRan_StillGivesTheXxHash3()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        EntityId row, entryId;
        using (var tx = connection.BeginTransaction())
        {
            var r = new GameInstallMetadata.New(tx) { Store = GameStore.Steam, Path = "/games/typed", Game = Game, Name = "Typed" };
            var result = await tx.Commit();
            row = result.Remap(r).Id;
        }
        TxId before;
        using (var tx = connection.BeginTransaction())
        {
            var e = Legacy(tx, row, "a.txt", 0xA1);
            var result = await tx.Commit();
            entryId = result[e];
            before = result.NewTx;
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var old = DiskStateEntry.Load(connection.AsOf(before), entryId);
        DiskStateEntry.Identity.Contains(old).Should().BeFalse("the migration had not run at that transaction");
        old.IdentityOf().Should().Be(FileIdentity.XxHash3(Hash.From(0xA1)));
    }

    [Fact]
    public async Task Migration_RunTwice_AddsNothingTheSecondTime()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        using (var tx = connection.BeginTransaction())
        {
            var r = new GameInstallMetadata.New(tx) { Store = GameStore.Steam, Path = "/games/typed", Game = Game, Name = "Typed" };
            var result = await tx.Commit();
            var row = result.Remap(r).Id;
            using var rowTx = connection.BeginTransaction();
            Legacy(rowTx, row, "a.txt", 0xA1);
            await rowTx.Commit();
        }

        await provider.GetRequiredService<MigrationService>().MigrateAll();
        var afterFirst = connection.Db.BasisTxId;
        await provider.GetRequiredService<MigrationService>().MigrateAll();

        connection.Db.BasisTxId.Should().Be(afterFirst, "a database at the latest version is not touched");
    }

    // The shape every build wrote before identities carried their kind.
    private static EntityId Legacy(ITransaction tx, EntityId installation, string path, ulong hash)
    {
        var id = tx.TempId(DiskStateEntry.EntryPartition);
        tx.Add(id, DiskStateEntry.Path, new GamePath(LocationId.Game, path).ToGamePathParentTuple(installation));
        tx.Add(id, DiskStateEntry.Game, installation);
        tx.Add(id, DiskStateEntry.XxHash3, Hash.From(hash));
        tx.Add(id, DiskStateEntry.Size, Size.From(1));
        tx.Add(id, DiskStateEntry.LastModified, DateTimeOffset.UnixEpoch);
        return id;
    }
}
