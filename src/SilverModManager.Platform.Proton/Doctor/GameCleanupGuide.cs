using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Platform.Proton.Doctor;

/// <summary>
/// A game's guided cleanup of an install another mod manager left behind, as text. Health Check shows it through
/// the <see cref="GameCleanupActions.ShowStepsId"/> action; the Fix action runs them after the user confirms the list, and the command line runs them with --apply.
/// </summary>
public interface IGameCleanupGuide
{
    /// <summary>Whether this guide is the one for the installation's game.</summary>
    bool AppliesTo(GameInstallation installation);

    /// <summary>The steps the loadout's installation needs and how to run them, one line each.</summary>
    IReadOnlyList<string> Describe(Loadout.ReadOnly loadout);
}

/// <summary>The diagnostic action that opens a <see cref="IGameCleanupGuide"/>.</summary>
public static class GameCleanupActions
{
    public const string ShowStepsId = "show-cleanup-steps";
    public const string FixId = "fix-cleanup";

    public static DiagnosticAction Fix() => new(FixId, "Fix", []);

    public static DiagnosticAction ShowSteps() => new(ShowStepsId, "Show cleanup steps", []);
}
