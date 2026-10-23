using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// Rows written before installs carried a game id of their own are keyed by their Nexus id alone.
/// The expected ids are spelled out here, not read from the game classes: they are what those
/// rows meant when they were written, whatever the classes say today.
/// </summary>
public class GameKeyMigrationTests
{
    [Theory]
    [InlineData(1704u, "CreationEngine.SkyrimSE")]
    [InlineData(1151u, "CreationEngine.Fallout4")]
    [InlineData(3474u, "Larian.BaldursGate3")]
    [InlineData(3174u, "Bannerlord")]
    [InlineData(3333u, "RedEngine.Cyberpunk2077")]
    [InlineData(1303u, "StardewValley")]
    public async Task MigrateAll_PreChangeRowOfACodedGame_GetsItsGameId(uint nexusId, string expectedGameId)
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var row = await WritePreChangeRow(connection, nexusId, "/games/one");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var migrated = GameInstallMetadata.Load(connection.Db, row);
        migrated.Game.Value.Should().Be(GameId.From(expectedGameId));
        migrated.GameId.Value.Should().Be(NexusModsGameId.From(nexusId), "Nexus-facing readers keep the id they had");
    }

    [Fact]
    public async Task MigrateAll_PreChangeRowOfAnUnknownGame_IsLeftWithoutGame()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        var known = await WritePreChangeRow(connection, 1704, "/games/skyrim");
        var unknown = await WritePreChangeRow(connection, 987654, "/games/unknown");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        GameInstallMetadata.Game.Contains(GameInstallMetadata.Load(connection.Db, unknown)).Should().BeFalse();
        GameInstallMetadata.Game.Contains(GameInstallMetadata.Load(connection.Db, known)).Should().BeTrue("one unknown row must not stop the others");
    }

    [Fact]
    public async Task MigrateAll_RunTwice_SecondRunWritesNothing()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();
        var connection = provider.GetRequiredService<IConnection>();
        await WritePreChangeRow(connection, 3333, "/games/cyberpunk");
        var migrations = provider.GetRequiredService<MigrationService>();

        await migrations.MigrateAll();
        var afterFirst = connection.Db.BasisTxId;
        await migrations.MigrateAll();

        connection.Db.BasisTxId.Should().Be(afterFirst);
        SchemaVersion.All(connection.Db).Single().CurrentVersion.Value.Should().BeGreaterThan(0);
    }

    private static async Task<EntityId> WritePreChangeRow(IConnection connection, uint nexusId, string path)
    {
        using var tx = connection.BeginTransaction();
        var row = new GameInstallMetadata.New(tx)
        {
            Store = GameStore.Steam,
            Path = path,
            GameId = NexusModsGameId.From(nexusId),
            Name = "Pre-change game",
        };
        var result = await tx.Commit();
        return result.Remap(row).Id;
    }
}
