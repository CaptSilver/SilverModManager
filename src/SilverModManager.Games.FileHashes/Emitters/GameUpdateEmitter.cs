using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Runs the update checks for games that say an update affects their mods. The game registry locates
/// installations once per process, so the store's build id and update time in the cached result are as
/// old as the app. This emitter locates the installation again, the way a sync does, and hands the checks
/// both results.
/// </summary>
public class GameUpdateEmitter(IEnumerable<IGameUpdateCheck> checks, ILogger<GameUpdateEmitter> logger) : ILoadoutDiagnosticEmitter
{
    private readonly IGameUpdateCheck[] _checks = checks.ToArray();

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.InstallationInstance;
        if (installation.Game is not IGameUpdateSensitivity sensitivity) yield break;

        var context = new GameUpdateContext(loadout, installation, sensitivity, Relocate(installation));
        foreach (var check in _checks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var diagnostic in check.Check(context))
                yield return diagnostic;
        }
    }

    /// <summary>
    /// The installation as its locator finds it now. When the locator no longer finds the game, the cached
    /// result stands in: a sync logs that as critical, but the Health Check only reads, so it stays quiet.
    /// </summary>
    private GameLocatorResult Relocate(GameInstallation installation)
    {
        var cached = installation.LocatorResult;
        if (cached.Locator.TryLocate(installation.Game, out var fresh)) return fresh;

        logger.LogDebug("Found no installation of `{Game}` again; the update checks use the result from startup", installation.Game.DisplayName);
        return cached;
    }
}
