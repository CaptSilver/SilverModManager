using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.App.UI.Settings;
using NexusMods.Backend;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Settings;

namespace NexusMods.DataModel.SchemaVersions.Tests;

/// <summary>
/// What the first start of the app writes into an empty data folder.
/// </summary>
public class FreshDataDirectoryTests
{
    private static bool HasSettingRow(IConnection connection, string name)
        => Setting.All(connection.Db).Any(x => x.Name == name);

    [Fact]
    public async Task Test_FreshDatabaseIsStampedAndOnlyHoldsForkNames()
    {
        using var folder = new OnDiskDataFolder();

        await using (var provider = folder.CreateProvider())
        {
            var migrations = provider.GetRequiredService<MigrationService>();
            await migrations.InitialSetup();

            var connection = provider.GetRequiredService<IConnection>();
            SchemaVersion.All(connection.Db).Single().IsValid().Should().BeTrue();

            var settings = provider.GetRequiredService<ISettingsManager>();
            settings.Set(new WelcomeSettings { HasShownWelcomeMessage = true });

            // The MnemonicDB backend saves on the scheduler, so wait for the row.
            var expected = "SilverModManager.App.UI.Settings.WelcomeSettings";
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && !HasSettingRow(connection, expected)) await Task.Delay(50);
            HasSettingRow(connection, expected).Should().BeTrue();
        }

        await using (var provider = folder.CreateProvider())
        {
            var migrations = provider.GetRequiredService<MigrationService>();
            await migrations.MigrateAll();

            var connection = provider.GetRequiredService<IConnection>();
            SchemaVersion.All(connection.Db).Single().IsValid().Should().BeTrue();

            var stored = connection.Db.AttributeCache.AllAttributeIds.Select(id => id.Namespace).Distinct();
            var allowed = SchemaFailsafes.AllowedLegacyNamespaces.Select(a => a.Namespace).ToHashSet();
            stored.Where(ns => !ns.StartsWith("SilverModManager.", StringComparison.Ordinal) && !allowed.Contains(ns))
                .Should().BeEmpty();
        }
    }
}
