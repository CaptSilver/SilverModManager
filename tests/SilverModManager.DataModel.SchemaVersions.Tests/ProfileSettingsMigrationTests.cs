using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// A loadout written before game-settings groups carried a marker names its MO2 profile settings group
/// "Profile settings" and nothing else says what the group is. The fixture is a data folder written by a build
/// from before the marker existed.
/// </summary>
public class ProfileSettingsMigrationTests
{
    private const string Fixture = "Resources/ProfileSettingsDatabase.zip";

    private static ServiceProvider OpenFixture(OnDiskDataFolder folder)
    {
        ZipFile.ExtractToDirectory(Path.Combine(AppContext.BaseDirectory, Fixture), folder.DatabaseFolder.ToString());
        return folder.CreateProvider();
    }

    private static LoadoutItemGroup.ReadOnly[] Groups(IConnection connection)
    {
        var loadout = Loadout.All(connection.Db).Single();
        return LoadoutItem.FindByLoadout(connection.Db, loadout).OfTypeLoadoutItemGroup().ToArray();
    }

    [Fact]
    public async Task MigrateAll_ProfileSettingsGroupOfAnOldLoadout_GetsTheMarker()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = OpenFixture(folder);
        var connection = provider.GetRequiredService<IConnection>();
        Groups(connection).Should().HaveCount(2).And.OnlyContain(g => !g.IsGameSettingsGroup, "the fixture predates the marker");

        await provider.GetRequiredService<MigrationService>().MigrateAll();

        var marked = Groups(connection).Where(g => g.IsGameSettingsGroup).ToArray();
        marked.Should().ContainSingle().Which.AsLoadoutItem().Name.Should().Be("Profile settings");
        Groups(connection).Single(g => !g.IsGameSettingsGroup).AsLoadoutItem().Name.Should().Be("Some Mod");
    }

    [Fact]
    public async Task MigrateAll_RenamingTheMarkedGroupAfterwards_KeepsTheMarker()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = OpenFixture(folder);
        var connection = provider.GetRequiredService<IConnection>();
        await provider.GetRequiredService<MigrationService>().MigrateAll();
        var marked = Groups(connection).Single(g => g.IsGameSettingsGroup);

        using (var tx = connection.BeginTransaction())
        {
            tx.Add(marked.Id, LoadoutItem.Name, "My INIs");
            await tx.Commit();
        }

        Groups(connection).Single(g => g.IsGameSettingsGroup).AsLoadoutItem().Name.Should().Be("My INIs");
    }

    [Fact]
    public async Task MigrateAll_RunTwice_SecondRunWritesNothing()
    {
        using var folder = new OnDiskDataFolder();
        await using var provider = OpenFixture(folder);
        var connection = provider.GetRequiredService<IConnection>();
        var migrations = provider.GetRequiredService<MigrationService>();

        await migrations.MigrateAll();
        var afterFirst = connection.Db.BasisTxId;
        await migrations.MigrateAll();

        connection.Db.BasisTxId.Should().Be(afterFirst);
    }
}
