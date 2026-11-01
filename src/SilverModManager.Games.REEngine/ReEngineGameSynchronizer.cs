using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// The default synchronizer, less the log REFramework writes while the game runs. Without the filter every session
/// shows up as changes to the loadout.
/// </summary>
public sealed class ReEngineGameSynchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider)
{
    protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(IsRuntimeOutput);

    /// <remarks>
    /// REFramework logs to the game folder under one name for every game (REFramework.cpp, get_persistent_dir), and keeps
    /// the previous run's log beside it.
    /// </remarks>
    public static bool IsRuntimeOutput(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;

        var relative = path.Path.ToString();
        return relative.Equals(ReFrameworkFiles.Log, StringComparison.OrdinalIgnoreCase)
               || relative.Equals(ReFrameworkFiles.PreviousLog, StringComparison.OrdinalIgnoreCase);
    }
}
