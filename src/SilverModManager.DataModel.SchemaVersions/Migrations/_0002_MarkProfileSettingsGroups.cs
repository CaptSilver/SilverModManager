using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;

namespace SilverModManager.DataModel.SchemaVersions;

/// <summary>
/// Marks the "Profile settings" group of every installed Wabbajack list as the loadout's game settings group.
/// </summary>
/// <remarks>
/// Before the marker existed the group was found by its name. The name is frozen here, because the group is now
/// free text the user can change and a new group is called something else.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0002_MarkProfileSettingsGroups(ILogger<_0002_MarkProfileSettingsGroups> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0002_MarkProfileSettingsGroups));

    private const string ProfileSettingsName = "Profile settings";

    public Task Prepare(IDb db) => Task.CompletedTask;

    public void Migrate(ITransaction tx, IDb db)
    {
        foreach (var group in LoadoutItemGroup.All(db))
        {
            if (group.IsGameSettingsGroup) continue;
            if (!group.AsLoadoutItem().Name.Equals(ProfileSettingsName, StringComparison.Ordinal)) continue;

            logger.LogDebug("Marking group {Group} as a game settings group", group.Id);
            tx.Add(group.Id, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        }
    }
}
