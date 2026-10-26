using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Emitters;

/// <summary>
/// Says which game INIs the next apply will take over for a loadout: files on disk the loadout owns but has no
/// copy of yet. That is the state before the first apply, while the player can still turn ownership off; once an
/// apply has taken the copies there is nothing to say.
/// </summary>
public class GameInisTakenOverEmitter(IGameIniFiles game) : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (!Loadout.OwnsGameInis.Get(loadout)) yield break;

        var copied = LoadoutIniState.FindByLoadout(loadout.Db, loadout.LoadoutId).Select(state => (GamePath)state.Path).ToHashSet();
        var locations = loadout.InstallationInstance.Locations;
        var waiting = GameInis.Paths(game)
            .Where(path => !copied.Contains(path) && locations.ContainsKey(path.LocationId) && locations.ToAbsolutePath(path).FileExists)
            .Select(path => path.FileName.ToString())
            .ToList();

        if (waiting.Count > 0)
            yield return Diagnostics.CreateGameInisTakenOver(string.Join(", ", waiting));
    }
}
