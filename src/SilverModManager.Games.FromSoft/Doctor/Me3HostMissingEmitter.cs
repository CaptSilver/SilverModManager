using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>Reports a loadout that has mods but no me3 to load them with.</summary>
public class Me3HostMissingEmitter : IFromSoftDiagnosticEmitter
{
    private const string Release = "me3-linux-amd64.tar.gz";

    public const string InstallActionId = "install-me3";

    /// <summary>The fix: downloads me3 and installs it into the loadout.</summary>
    public static DiagnosticAction InstallAction() => new(InstallActionId, "Install me3", []);

    // The check reads the loadout's items, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (loadout.InstallationInstance.Game is IFromSoftGame game && game.Loader.IsModded(loadout) && !game.Loader.HasHost(loadout))
            yield return FromSoftDiagnostics.CreateMe3HostMissing(Release, game.DisplayName) with { Actions = [InstallAction()] };
    }
}
