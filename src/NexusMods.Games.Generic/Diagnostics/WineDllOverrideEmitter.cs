using System.Runtime.CompilerServices;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Generic.Diagnostics;

/// <summary>
/// Reports the Wine DLL overrides a game declares through <see cref="IWineDllOverrideRequirements"/> that the
/// prefix does not have yet. It clears once the prefix carries them.
/// </summary>
/// <remarks>
/// The check compares against what the prefix and the Steam launch options line carry, not against the environment
/// the app sets when it launches the game itself, so it keeps showing until the line is written even though a
/// launch from the app would already have the override.
/// </remarks>
public class WineDllOverrideEmitter : ILoadoutDiagnosticEmitter
{
    /// <inheritdoc />
    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var installation = loadout.InstallationInstance;
        var game = installation.Game;

        var requiredOverrides = WineDllOverrideRequirements.For(game, loadout);
        if (requiredOverrides.Count == 0) yield break;

        var instructions = await WineDiagnosticHelper.GetWineDllOverridesUpdateInstructions(
            installation,
            requiredOverrides,
            GameLauncherSkip.For(game),
            cancellationToken
        );
        if (instructions is null) yield break;

        yield return Diagnostics.CreateRequiredWineDllOverrides(GameName: game.DisplayName, Instructions: instructions)
            with { Actions = WineDiagnosticHelper.GetWineDllOverridesFixActions(installation) };
    }
}
