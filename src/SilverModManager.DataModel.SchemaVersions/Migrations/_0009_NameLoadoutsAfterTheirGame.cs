using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.DataModel.SchemaVersions;

/// <summary>
/// Renames loadouts that still carry the automatic name an older build gave them ("Loadout A", "Loadout B", ...)
/// to the game's name, numbered the way a new loadout is: "Game", "Game 2", and so on.
/// </summary>
/// <remarks>
/// Only loadouts the user can see take part: a leftover deleted or vanilla-state row is neither renamed nor counts
/// its name as taken, the same as when a new loadout is named. The game name is the one stored on the installation row. Loadouts are numbered per installation, oldest first,
/// and skip any name another loadout of the installation already has, so a name the user chose is never touched or
/// duplicated. The short name badge is not changed.
/// </remarks>
// ReSharper disable once InconsistentNaming
public sealed class _0009_NameLoadoutsAfterTheirGame(ILogger<_0009_NameLoadoutsAfterTheirGame> logger) : ITransactionalMigration
{
    public static (MigrationId Id, string Name) IdAndName => MigrationId.ParseNameAndId(nameof(_0009_NameLoadoutsAfterTheirGame));

    public Task Prepare(IDb db) => Task.CompletedTask;

    public void Migrate(ITransaction tx, IDb db)
    {
        foreach (var installation in Loadout.All(db).Where(loadout => loadout.IsVisible()).GroupBy(loadout => loadout.InstallationId.Value))
        {
            var loadouts = installation.OrderBy(loadout => loadout.Id.Value).ToArray();
            var automatic = loadouts.Where(loadout => LoadoutNameProvider.IsLegacyDefaultName(loadout.Name)).ToArray();
            if (automatic.Length == 0) continue;

            var gameName = GameInstallMetadata.Load(db, installation.Key).Name;
            var taken = loadouts.Except(automatic).Select(loadout => loadout.Name).ToList();
            foreach (var loadout in automatic)
            {
                var newName = LoadoutNameProvider.GetDefaultName(gameName, taken);
                logger.LogInformation("Renaming loadout {Loadout} from `{OldName}` to `{NewName}`", loadout.Id, loadout.Name, newName);
                tx.Add(loadout.Id, Loadout.Name, newName);
                taken.Add(newName);
            }
        }
    }
}
