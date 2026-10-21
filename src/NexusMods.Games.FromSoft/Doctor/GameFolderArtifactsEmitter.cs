using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.FromSoft.EldenRing;
using NexusMods.Platform.Proton.Doctor;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// Reports mod files in the real game folder: Critical when an online vanilla launch could load them or the folder
/// still carries an old launcher swap, a Warning when the launcher that would start them is gone.
/// </summary>
/// <remarks>
/// Both findings carry "Show cleanup steps", which opens the steps of the guided cleanup.
/// </remarks>
public class GameFolderArtifactsEmitter : AEldenRingCheckEmitter
{
    protected override IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout)
    {
        // A missing folder is a broken install, not mod files; the doctor report says so, this diagnostic's title would not.
        var gameFolder = EldenRingPaths.GameFolder(loadout.InstallationInstance);
        if (!gameFolder.DirectoryExists()) return [];

        var artifacts = EldenRingChecks.GameFolder(gameFolder).First(r => r.Id == "artifacts");

        return artifacts.Level switch
        {
            CheckLevel.Fail => [FromSoftDiagnostics.CreateGameFolderArtifacts(artifacts.Message) with { Actions = [GameCleanupActions.ShowSteps()] }],
            CheckLevel.Warn => [FromSoftDiagnostics.CreateGameFolderArtifacts(artifacts.Message) with { Severity = DiagnosticSeverity.Warning, Actions = [GameCleanupActions.ShowSteps()] }],
            _ => [],
        };
    }
}
