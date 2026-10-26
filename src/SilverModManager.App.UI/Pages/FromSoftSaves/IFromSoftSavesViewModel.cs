using System.Reactive;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Games.FromSoft.Saves;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.FromSoftSaves;

/// <summary>
/// The saves page of an Elden Ring loadout: the game's save folder as it is on disk, with backup, restore and
/// quarantine of the files the module knows. Files it does not know are listed and never acted on.
/// </summary>
public interface IFromSoftSavesViewModel : IPageViewModelInterface
{
    LoadoutId LoadoutId { get; set; }

    /// <summary>Why no save folder is shown, empty when one is (or when the game has not written any yet).</summary>
    string Problem { get; }

    /// <summary>One entry per Steam account that has a folder in the prefix.</summary>
    IReadOnlyList<SaveFolder> Folders { get; }

    SaveFolder? SelectedFolder { get; set; }

    /// <summary>The loadouts whose saves are in the folders, led by "all saves" and "not tied to a loadout".</summary>
    IReadOnlyList<LoadoutFilterItem> LoadoutFilters { get; }

    LoadoutFilterItem? SelectedLoadoutFilter { get; set; }

    /// <summary>The name of the loadout a save belongs to; empty for a save no loadout owns.</summary>
    string LoadoutNameOf(SaveFileEntry file);

    /// <summary>The files of <see cref="SelectedFolder"/> that belong to <see cref="SelectedLoadoutFilter"/>, the ones the module knows first.</summary>
    IReadOnlyList<SaveFileEntry> Files { get; }

    SaveFileEntry? SelectedFile { get; set; }

    /// <summary>Every copy kept for the installation: backups, restore safety copies, and quarantined saves.</summary>
    IReadOnlyList<SaveBackup> Backups { get; }

    SaveBackup? SelectedBackup { get; set; }

    bool IsSteamRunning { get; }

    /// <summary>Whether <see cref="QuarantineCommand"/> can run: the vanilla save is selected and Steam is closed.</summary>
    bool CanQuarantine { get; }

    /// <summary>Why <see cref="QuarantineCommand"/> is disabled, empty when it is not.</summary>
    string QuarantineDisabledReason { get; }

    /// <summary>What restoring the selected copy would do, asked of the user. Empty when no restore is waiting for an answer.</summary>
    string RestorePrompt { get; }

    /// <summary>What the last operation did, empty before the first.</summary>
    string Message { get; }

    /// <summary>What is left for the user after the last operation, such as the Steam Cloud purge instructions.</summary>
    IReadOnlyList<string> Notes { get; }

    /// <summary>Whether the launch gate could not restore the vanilla save after a modded session.</summary>
    bool HasTaintedSave { get; }

    string TaintedMessage { get; }

    ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    /// <summary>Copies the selected file into the backups folder.</summary>
    ReactiveCommand<Unit, Unit> BackupCommand { get; }

    /// <summary>Asks before restoring the selected copy; nothing is written until <see cref="ConfirmRestoreCommand"/>.</summary>
    ReactiveCommand<Unit, Unit> RestoreCommand { get; }

    ReactiveCommand<Unit, Unit> ConfirmRestoreCommand { get; }

    ReactiveCommand<Unit, Unit> CancelRestoreCommand { get; }

    ReactiveCommand<Unit, Unit> QuarantineCommand { get; }

    /// <summary>Settles a tainted save once the user has dealt with it, which lets an unmodded launch through again.</summary>
    ReactiveCommand<Unit, Unit> ResolveCommand { get; }
}
