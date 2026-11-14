using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// Reports a game that is still running, or a tool still open in its prefix, while the last launch's changes are
/// owed to the loadout. The pending-finish marker is written when a launch starts running, so it is only a finding
/// when nothing in this process or another one holds the game.
/// </summary>
/// <remarks>
/// The wording does not claim the processes came from the last session: a stale marker from a crashed session and a
/// game started from Steam since look the same from here.
/// </remarks>
public sealed class UnfinishedLaunchEmitter(IGameLaunchSession sessions, ILaunchFinishMarkers markers, IRunningGameDetector detector, IProcessTracker tracker) : ILoadoutDiagnosticEmitter
{
    /// <inheritdoc />
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => DiagnoseAsync(loadout, cancellationToken);

    private async IAsyncEnumerable<Diagnostic> DiagnoseAsync(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // The process table and the lock file are read off the caller's thread.
        await Task.Yield();
        foreach (var diagnostic in Evaluate(loadout.InstallationInstance, loadout.Name)) yield return diagnostic;
    }

    internal IEnumerable<Diagnostic> Evaluate(GameInstallation installation, string shownLoadoutName)
    {
        // A launch of ours, or another process's lock, is an ordinary session in progress and not an unfinished one.
        if (sessions.Describe(installation) is RunningInThisProcess or HeldByOtherProcess) yield break;
        // AlreadySynced and LoadoutGone are the finisher's to clear; nothing here is owed.
        if (markers.Read(installation) is not { State: PendingLaunchFinishState.Pending } marker) yield break;

        var game = installation.Game.DisplayName;
        // The changes go into the marked loadout, which can be another one than the loadout whose Health Check this is.
        var name = marker.LoadoutName ?? shownLoadoutName;

        if (PendingLaunchFinisher.SteamIsUpdating(installation, tracker))
        {
            yield return UnfinishedLaunchDiagnostics.CreateSteamUpdatingAfterUnfinishedLaunch(RunningGameWording.SteamUpdating(game, name));
            yield break;
        }

        var running = detector.Running(installation);
        if (running.Game.Count > 0)
        {
            var first = running.Game[0];
            yield return UnfinishedLaunchDiagnostics.CreateGameRunningAfterUnfinishedLaunch(
                $"{game} is running ({first.Name}, pid {first.Pid}) and the changes from its last session are not in '{name}' yet. If you started it yourself, quit it and the app finishes on its own.")
                with { Actions = [DiagnosticActions.StopGameAndFinish(game)] };
        }
        else if (running.Tools.Count > 0)
        {
            var tool = running.Tools[0];
            yield return UnfinishedLaunchDiagnostics.CreateToolOpenAfterUnfinishedLaunch(
                $"Close {tool.Name} ({tool.Pid}); it is open in {game}'s prefix, and the changes from the last session are taken into '{name}' once it is.");
        }
    }
}
