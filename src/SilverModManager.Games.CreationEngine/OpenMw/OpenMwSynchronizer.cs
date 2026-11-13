using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Keeps the Morrowind install out of the sync: it is read for plugin headers and LOOT, never indexed, backed up or
/// written, which is what lets a Morrowind row manage the same install without either row ingesting the other's files.
/// </summary>
public class OpenMwSynchronizer(IServiceProvider provider, OpenMw game) : BethesdaSynchronizer(provider, game)
{
    protected override IGamePathFilter GameSpecificPathFilter { get; } =
        GamePathFilters.Create(path => game.IsProtectedPath(path) || path.LocationId == LocationId.Game);

    public override bool IsIgnoredBackupPath(GamePath path) =>
        path.LocationId == LocationId.Game || base.IsIgnoredBackupPath(path);

    /// <summary>
    /// Refuses before anything is written when openmw.cfg is gone. Only the launcher can make a config that points at
    /// the Morrowind install, and a plugin list needs somewhere to live.
    /// </summary>
    protected override Task PreflightLoadout(Loadout.ReadOnly loadout)
    {
        var config = loadout.InstallationInstance.Locations.ToAbsolutePath(OpenMwConstants.OpenMwConfigFile);
        if (!config.FileExists) throw new OpenMwConfigMissingException(config.Parent);
        return base.PreflightLoadout(loadout);
    }
}
