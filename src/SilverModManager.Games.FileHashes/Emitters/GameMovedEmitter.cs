using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FileHashes.Emitters;

/// <summary>
/// Tells the user once that a game is in another folder than the one it was managed in. The game registry
/// records the old folder on the installation when it follows a move; dismissing the note removes it, so
/// nothing is shown again until the game moves again.
/// </summary>
public class GameMovedEmitter : ILoadoutDiagnosticEmitter
{
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var installation = loadout.Installation;
        if (!GameInstallMetadata.MovedFrom.TryGetValue(installation, out var oldPath)) yield break;

        yield return Diagnostics.CreateGameMoved(
            Game: installation.Name,
            OldPath: oldPath,
            NewPath: installation.Path
        ) with { Actions = [DiagnosticActions.DismissGameMoved()] };
    }
}
