using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.Unity;

/// <summary>
/// The default synchronizer, less the files BepInEx writes while the game runs. Without the filter every session
/// shows up as changes to the loadout.
/// </summary>
public class UnityGameSynchronizer(IServiceProvider provider) : ALoadoutSynchronizer(provider)
{
    protected override IGamePathFilter GameSpecificPathFilter { get; } = GamePathFilters.Create(IsRuntimeOutput);

    /// <remarks>
    /// These are the paths BepInEx is expected to write at runtime: its log and the type cache under <c>BepInEx/cache</c>.
    /// Not checked against the BepInEx source; a played session confirms them.
    /// </remarks>
    public static bool IsRuntimeOutput(GamePath path)
    {
        if (path.LocationId != LocationId.Game) return false;

        var relative = path.Path.ToString();
        return relative.Equals("BepInEx/LogOutput.log", StringComparison.OrdinalIgnoreCase)
               || relative.StartsWith("BepInEx/cache/", StringComparison.OrdinalIgnoreCase);
    }
}
