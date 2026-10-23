using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App;
using SilverModManager.App.UI.Settings;
using SilverModManager.App.UI.Windows;
using SilverModManager.Backend;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.DataModel.SchemaVersions.Tests;

/// <summary>
/// The headless and GUI service graphs open the same data folder, so whatever one writes the
/// other has to read back.
/// </summary>
public class HeadlessGuiRoundTripTests
{
    private const string WelcomeSettingName = "SilverModManager.App.UI.Settings.WelcomeSettings";
    private const string DiagnosticSettingName = "SilverModManager.Abstractions.Diagnostics.DiagnosticSettings";

    private const string WindowDataNamespace = "SilverModManager.App.UI.Windows.WindowData";

    [Fact]
    public async Task Test_HeadlessGraphReadsDatabaseCreatedByGui()
    {
        using var folder = new OnDiskDataFolder();

        await using (var provider = folder.CreateProvider(OnDiskDataFolder.Gui))
        {
            await provider.GetRequiredService<MigrationService>().InitialSetup();
            var connection = provider.GetRequiredService<IConnection>();

            using (var tx = connection.BeginTransaction())
            {
                _ = new WindowDataAttributes.New(tx) { Data = "{}" };
                await tx.Commit();
            }
            WindowDataAttributes.All(connection.Db).Should().HaveCount(1);

            provider.GetRequiredService<ISettingsManager>().Set(new WelcomeSettings { HasShownWelcomeMessage = true });
            await SettingRows.WaitFor(connection, WelcomeSettingName);
        }

        await using (var provider = folder.CreateProvider(OnDiskDataFolder.Headless))
        {
            await provider.GetRequiredService<MigrationService>().MigrateAll();
            var connection = provider.GetRequiredService<IConnection>();
            var db = connection.Db;

            SchemaVersion.All(db).Single().IsValid().Should().BeTrue();
            SettingRows.Has(connection, WelcomeSettingName).Should().BeTrue();
            Loadout.All(db).ToArray().Should().BeEmpty();
            GameInstallMetadata.All(db).ToArray().Should().BeEmpty();

            // UI-only attributes are persisted schema, not code the headless graph has to know.
            db.AttributeCache.AllAttributeIds
                .Where(id => id.Namespace == WindowDataNamespace)
                .Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task Test_GuiGraphReadsDatabaseCreatedHeadless()
    {
        using var folder = new OnDiskDataFolder();

        await using (var provider = folder.CreateProvider(OnDiskDataFolder.Headless))
        {
            await provider.GetRequiredService<MigrationService>().InitialSetup();

            // WelcomeSettings is a GUI-only setting the headless manager does not know, so use one both graphs register.
            var connection = provider.GetRequiredService<IConnection>();
            provider.GetRequiredService<ISettingsManager>().Set(new DiagnosticSettings { MinimumSeverity = DiagnosticSeverity.Warning });
            await SettingRows.WaitFor(connection, DiagnosticSettingName);
        }

        await using (var provider = folder.CreateProvider(OnDiskDataFolder.Gui))
        {
            await provider.GetRequiredService<MigrationService>().MigrateAll();
            var connection = provider.GetRequiredService<IConnection>();

            SchemaVersion.All(connection.Db).Single().IsValid().Should().BeTrue();
            WindowDataAttributes.All(connection.Db).ToArray().Should().BeEmpty();
            SettingRows.Has(connection, DiagnosticSettingName).Should().BeTrue();
        }
    }
}
