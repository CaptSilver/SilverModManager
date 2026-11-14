using Microsoft.Extensions.Logging;
using SilverModManager.Sdk.EventBus;
using SilverModManager.Sdk.Games;

namespace SilverModManager.CLI.Types.IpcHandlers;

/// <summary>
/// Which managed game a link that names none installs into. Several handlers face the same question, so the answer
/// and the toast that explains a missing one live here.
/// </summary>
internal static class LinkTarget
{
    /// <summary>
    /// The loadout of the one installation that <paramref name="isGame"/> accepts. A link that fits several installs
    /// takes the one the user has applied a loadout to; with none or more than one in use there is no telling, and the
    /// failure is sent to the event bus. Returns null when it was.
    /// </summary>
    /// <param name="linked">What the link names, for the message that it fits several games.</param>
    /// <param name="wanted">What the link needs a game to have, for the message that no managed game has it.</param>
    public static ManagedLoadout? Choose(ManagedGames managedGames, Func<IGameData, bool> isGame, string linked, string wanted, IEventBus eventBus, ILogger logger)
    {
        var candidates = managedGames.InstallationCandidates(isGame);
        var inUse = candidates.Count > 1 ? candidates.Where(candidate => candidate.HasAppliedLoadout).ToList() : candidates;
        if (candidates.Count == 0)
        {
            logger.LogWarning("Mod download aborted: no managed game takes mods from {Wanted}", wanted);
            eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.GameNotManaged(wanted)));
            return null;
        }

        // Several games take the link: the one the user has applied a loadout to is the one in use.
        if (inUse.Count != 1)
        {
            var games = candidates.Select(candidate => candidate.Installation.Game.DisplayName).Distinct().ToList();
            eventBus.Send(new CliMessages.ModDownloadFailed(new FailureReason.Ambiguous(linked, games)));
            return null;
        }

        return managedGames.LoadoutOf(inUse[0].Installation);
    }
}
