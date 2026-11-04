using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.FromSoft.Diagnostics;
using SilverModManager.Games.FromSoft.Overlay;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// Reports what the last modded session of the loadout left unresolved: files in its overlay that are not runtime
/// files, files that hide a game file, and changes to the real game folder. Each carries a Discard action.
/// </summary>
public class UpperdirAuditEmitter(ISettingsManager settings, IFileSystem fileSystem) : IFromSoftDiagnosticEmitter
{
    /// <summary>The id of the action that resolves a finding. The arguments are the loadout id, the finding's kind and its path.</summary>
    public const string DiscardActionId = "discard-overlay-finding";

    // Reads the overlay's findings file, not the sync tree, so it gives the same answer without one.
    public IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, CancellationToken cancellationToken)
        => Diagnose(loadout, FrozenDictionary<GamePath, SyncNode>.Empty, cancellationToken);

    public async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var layout = OverlayLayout.For(settings.Get<FromSoftSettings>(), fileSystem, loadout.InstallationInstance, loadout.LoadoutId);
        foreach (var finding in UpperdirAudit.PendingFindings(layout))
        {
            Diagnostic diagnostic = finding.Kind == AuditFindingKind.GameFolderChanged
                ? FromSoftDiagnostics.CreateGameFolderChangedInSession(finding.Path, finding.Detail)
                : FromSoftDiagnostics.CreateOverlayFileLeftBehind(finding.Path, Reason(finding));

            yield return diagnostic with { Actions = [DiscardAction(loadout.LoadoutId, finding)] };
        }
    }

    public static DiagnosticAction DiscardAction(LoadoutId loadoutId, AuditFinding finding)
        => new(DiscardActionId, "Discard", [loadoutId.Value.Value.ToString(), finding.Kind.ToString(), finding.Path]);

    /// <summary>
    /// Runs <see cref="DiscardActionId"/>: deletes the upper-folder file the finding names (a game-folder finding has
    /// none) and drops the finding. Only a finding that is still pending is acted on, so a stale or hand-made action
    /// can never delete a file the audit did not list.
    /// </summary>
    public void Discard(Loadout.ReadOnly loadout, ImmutableArray<string> arguments)
    {
        if (arguments.Length != 3 || !Enum.TryParse<AuditFindingKind>(arguments[1], out var kind))
            throw new ArgumentException("Expected the loadout id, the finding's kind and its path.", nameof(arguments));

        var layout = OverlayLayout.For(settings.Get<FromSoftSettings>(), fileSystem, loadout.InstallationInstance, loadout.LoadoutId);
        var pending = UpperdirAudit.PendingFindings(layout).FirstOrDefault(f => f.Kind == kind && f.Path == arguments[2]);
        if (pending is not null) UpperdirAudit.Discard(layout, pending);
    }

    private static string Reason(AuditFinding finding) => finding.Kind == AuditFindingKind.ShadowedGameFile
        ? "The game folder has a file at the same path, so this one replaces it, or hides it if it marks a deletion."
        : "It is not a mod's own file, a crash dump, a log or a cache.";
}
