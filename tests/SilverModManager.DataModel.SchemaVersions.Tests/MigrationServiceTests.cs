using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

public class MigrationServiceTests
{
    [Fact]
    public async Task Test_InitialSetupStampsTheNewestMigration()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = folder.CreateProvider();

        var migrations = provider.GetRequiredService<MigrationService>();
        await migrations.InitialSetup();
        await migrations.MigrateAll();

        var connection = provider.GetRequiredService<IConnection>();
        SchemaVersion.All(connection.Db).Single().CurrentVersion.Should().Be(provider.GetServices<MigrationDefinition>().Max(m => m.Id));
    }
}
