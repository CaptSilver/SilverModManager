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

            await SettingRows.WaitFor(connection, "SilverModManager.App.UI.Settings.WelcomeSettings");
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
