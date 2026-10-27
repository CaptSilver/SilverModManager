using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.MnemonicDB.Storage;
using SilverModManager.MnemonicDB.Storage.Abstractions;
using SilverModManager.Sdk.Games;
using RocksDbBackend = SilverModManager.MnemonicDB.Storage.RocksDbBackend.Backend;

namespace SilverModManager.DataModel.Tests;

/// <summary>
/// A retraction names a value it expects the entity to hold. When it does not, committing it would write a
/// retraction of nothing into history and leave the value the caller meant to remove (or one it never saw) current.
/// </summary>
public class DatomStoreRetractionTests
{
    private static ServiceProvider CreateProvider() => new ServiceCollection()
        .AddLogging()
        .AddMnemonicDB()
        .AddDatomStoreSettings(new DatomStoreSettings())
        .AddSingleton<IStoreBackend, RocksDbBackend>()
        .AddGameInstallMetadataModel()
        .BuildServiceProvider();

    private static async Task<EntityId> EntityNamed(IConnection connection, string name)
    {
        using var tx = connection.BeginTransaction();
        var id = tx.TempId();
        tx.Add(id, GameInstallMetadata.Name, name);
        var result = await tx.Commit();
        return result[id];
    }

    private static async Task Rename(IConnection connection, EntityId id, string name)
    {
        using var tx = connection.BeginTransaction();
        tx.Add(id, GameInstallMetadata.Name, name);
        await tx.Commit();
    }

    [Fact]
    public async Task RetractingAValueThatWasAlreadyReplaced_FailsTheCommitAndWritesNothing()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var id = await EntityNamed(connection, "v0");
        await Rename(connection, id, "v1");
        var before = connection.Db.BasisTxId;

        using (var tx = connection.BeginTransaction())
        {
            tx.Retract(id, GameInstallMetadata.Name, "v0");
            var act = async () => await tx.Commit();
            (await act.Should().ThrowAsync<RetractionMismatchException>()).Which.Message.Should().Contain("v0").And.Contain(GameInstallMetadata.Name.Id.ToString());
        }

        connection.Db.BasisTxId.Should().Be(before, "the failed transaction wrote nothing");
        GameInstallMetadata.Load(connection.Db, id).Name.Should().Be("v1");

        await Rename(connection, id, "v2");
        GameInstallMetadata.Load(connection.Db, id).Name.Should().Be("v2", "the store still takes transactions");
    }

    [Fact]
    public async Task RetractingAValueAddedInTheSameTransaction_FailsTheCommit()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var id = await EntityNamed(connection, "v0");

        using (var tx = connection.BeginTransaction())
        {
            // Retractions are matched against the database before the transaction, which never held x1.
            tx.Add(id, GameInstallMetadata.Name, "x1");
            tx.Retract(id, GameInstallMetadata.Name, "x1");
            var act = async () => await tx.Commit();
            await act.Should().ThrowAsync<RetractionMismatchException>();
        }

        GameInstallMetadata.Load(connection.Db, id).Name.Should().Be("v0");
    }

    // A toggle decides from what it last saw. By the time its transaction lands the value may have changed or gone.
    [Fact]
    public async Task RetractCurrent_ValueReplacedBeforeTheCommit_RetractsTheValueThenHeld()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var id = await EntityNamed(connection, "v0");

        using var tx = connection.BeginTransaction();
        tx.RetractCurrent(id, GameInstallMetadata.Name);
        await Rename(connection, id, "v1");
        await tx.Commit();

        GameInstallMetadata.Name.Contains(GameInstallMetadata.Load(connection.Db, id)).Should().BeFalse();
    }

    [Fact]
    public async Task RetractCurrent_ValueAlreadyGone_CommitsAndChangesNothing()
    {
        await using var provider = CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var id = await EntityNamed(connection, "v0");
        using var first = connection.BeginTransaction();
        using var second = connection.BeginTransaction();
        first.RetractCurrent(id, GameInstallMetadata.Name);
        second.RetractCurrent(id, GameInstallMetadata.Name);
        await first.Commit();

        await second.Commit();

        connection.Db.Get(id).Should().BeEmpty();
    }
}
