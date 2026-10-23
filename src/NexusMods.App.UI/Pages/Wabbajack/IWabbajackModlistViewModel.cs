using System.Reactive;
using Avalonia.Media.Imaging;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Library;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Wabbajack;

/// <summary>
/// A modlist file of the library: what is in it, what is still missing for it, and the install into a loadout.
/// </summary>
public interface IWabbajackModlistViewModel : IPageViewModelInterface
{
    LibraryFileId FileId { get; set; }

    string Name { get; }
    string Author { get; }
    string Version { get; }
    string GameName { get; }

    /// <summary>Archive count and download size, e.g. "312 archives, 48.2 GB".</summary>
    string ArchiveSummary { get; }

    Bitmap? Banner { get; }

    IReadOnlyList<WabbajackLoadoutChoice> LoadoutChoices { get; }
    WabbajackLoadoutChoice? SelectedLoadout { get; }

    /// <summary>Picks the loadout to install into; the "Create new loadout" entry adds one and picks that.</summary>
    ReactiveCommand<WabbajackLoadoutChoice, Unit> SelectLoadoutCommand { get; }

    IReadOnlyList<WabbajackArchiveRow> Archives { get; }

    /// <summary>The line above the archive table: what is still to get, or downloading progress.</summary>
    string AcquireText { get; }

    /// <summary>The inbox files the last check could not match to an archive, or empty.</summary>
    string InboxText { get; }

    string ExtractedSizeText { get; }
    string FreeSpaceText { get; }
    bool IsFreeSpaceWarning { get; }

    /// <summary>Files of the game folder the list overwrites.</summary>
    IReadOnlyList<WabbajackGameFileChoice> GameFilesReplaced { get; }

    bool SkipMissingArchives { get; set; }

    bool CanInstall { get; }
    bool IsInstalling { get; }
    string ProgressText { get; }
    double ProgressFraction { get; }
    string ErrorText { get; }

    /// <summary>The list is in the selected loadout but disabled, because an earlier install did not finish or skipped files.</summary>
    bool CanResume { get; }

    /// <summary>The list is in the selected loadout.</summary>
    bool IsInstalled { get; }

    /// <summary>The files the last install of this list in the selected loadout skipped.</summary>
    IReadOnlyList<WabbajackSkippedRow> SkippedFiles { get; }

    /// <summary>Installs, or continues an install, into <see cref="SelectedLoadout"/>.</summary>
    ReactiveCommand<Unit, Unit> InstallCommand { get; }

    ReactiveCommand<WabbajackArchiveRow, Unit> OpenPageCommand { get; }

    /// <summary>Opens the pages of the next five Nexus Mods files that still need a download in the browser.</summary>
    ReactiveCommand<Unit, Unit> OpenNextFiveCommand { get; }

    ReactiveCommand<Unit, Unit> CheckInboxCommand { get; }
    ReactiveCommand<Unit, Unit> OpenInboxFolderCommand { get; }
    ReactiveCommand<Unit, Unit> OpenHealthCheckCommand { get; }

    /// <summary>Removes the list's mods from the loadout.</summary>
    ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>Copies files the user dropped on the page into the inbox and checks it.</summary>
    Task AddDroppedFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}
