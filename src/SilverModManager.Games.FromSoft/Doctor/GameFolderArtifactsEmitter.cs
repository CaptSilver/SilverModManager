using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Platform.Proton.Doctor;

namespace SilverModManager.Games.FromSoft.Doctor;

/// <summary>
/// Reports mod files in the real game folder: Critical when an online vanilla launch could load them or the folder
/// still carries an old launcher swap, a Warning when the launcher that would start them is gone.
/// </summary>
/// <remarks>
/// Both findings carry "Show cleanup steps", which opens the steps of the guided cleanup.
/// </remarks>
public class GameFolderArtifactsEmitter : AFromSoftCheckEmitter
{
    protected override IEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, IFromSoftGame game)
    {
        // A missing folder is a broken install, not mod files; the doctor report says so, this diagnostic's title would not.
        var gameFolder = FromSoftPaths.GameFolder(game.Layout, loadout.InstallationInstance);
        if (!gameFolder.DirectoryExists()) return [];

        var artifacts = FromSoftChecks.GameFolder(gameFolder, game.CheckRules).First(r => r.Id == "artifacts");

        return artifacts.Level switch
        {
            CheckLevel.Fail => [FromSoftDiagnostics.CreateGameFolderArtifacts(artifacts.Message, game.DisplayName) with { Actions = [GameCleanupActions.ShowSteps()] }],
            CheckLevel.Warn => [FromSoftDiagnostics.CreateGameFolderArtifacts(artifacts.Message, game.DisplayName) with { Severity = DiagnosticSeverity.Warning, Actions = [GameCleanupActions.ShowSteps()] }],
            _ => [],
        };
    }
}
