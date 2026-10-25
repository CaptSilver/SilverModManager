using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Notes that the store has an update for the game that has not installed yet. It reads the store's target
/// build against the installed one rather than <see cref="SilverModManager.Sdk.Games.GameLocatorResult.StoreUpdatePending"/>,
/// because Steam leaves manifests flagged as updating indefinitely, some of them with the target equal to
/// the installed build. It does not depend on the loadout: the update is coming whether or not mods were
/// applied, and it clears on its own once the store installs it.
/// </summary>
public class GameUpdateWaitingCheck : IGameUpdateCheck
{
    private const string UpdateOnLaunchAdvice =
        "To take the update on your own schedule, set the game's Steam update setting to \"Only update this game when I launch it\".";

    private const string InstallsOnLaunch = "Steam will install it the next time you launch the game through Steam.";

    public IEnumerable<Diagnostic> Check(GameUpdateContext context)
    {
        var fresh = context.Fresh;
        if (fresh.StoreTargetBuildId is not { } target || target == fresh.StoreBuildId) yield break;

        yield return Diagnostics.CreateGameUpdateWaiting(
            Game: context.Installation.Game.DisplayName,
            Store: fresh.Store.Value,
            WhenItInstalls: fresh.StoreUpdatesInBackground ? UpdateOnLaunchAdvice : InstallsOnLaunch
        );
    }
}
