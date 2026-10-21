using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Sdk.Loadouts;
using NexusMods.Games.FromSoft.EldenRing;

namespace NexusMods.Games.FromSoft.Doctor;

/// <summary>
/// Reports mod files in the real game folder: Critical when an online vanilla launch could load them or the folder
/// still carries an old launcher swap, a Warning when the launcher that would start them is gone.
/// </summary>
/// <remarks>
/// There is no fix action yet: the guided cleanup of an old mod-manager install does not exist, and an action that
/// opened nothing would be a button that does nothing.
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
            CheckLevel.Fail => [FromSoftDiagnostics.CreateGameFolderArtifacts(artifacts.Message)],
            CheckLevel.Warn => [FromSoftDiagnostics.CreateGameFolderArtifacts(artifacts.Message) with { Severity = DiagnosticSeverity.Warning }],
            _ => [],
        };
    }
}
