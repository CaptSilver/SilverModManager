using System.Reactive;
using Avalonia.Media.Imaging;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>
/// A modlist file of the library: what is in it, what is still missing for it, and the install into a loadout.
/// </summary>
public interface IWabbajackModlistViewModel : IPageViewModelInterface
{
    LibraryFileId FileId { get; set; }

    /// <summary>The loadout to pick once the list is read, if it is one of the list's game; otherwise the first one is.</summary>
    LoadoutId? PreferredLoadout { get; set; }

    string Name { get; }
    string Author { get; }
    string Version { get; }
    string GameName { get; }

    /// <summary>E.g. "312 archives".</summary>
    string ArchiveCountText { get; }

    /// <summary>The download size of every archive, e.g. "48.2 GB".</summary>
    string ArchiveSizeText { get; }

    /// <summary>The list's own description, or empty.</summary>
    string Description { get; }

    /// <summary>The list's readme, when it gives a link to one.</summary>
    Uri? ReadmeUri { get; }

    ReactiveCommand<Unit, Unit> OpenReadmeCommand { get; }

    /// <summary>The single line under the toolbar: an error, progress, or the one thing that blocks the buttons.</summary>
    WabbajackStatus Status { get; }

    Bitmap? Banner { get; }

    IReadOnlyList<WabbajackLoadoutChoice> LoadoutChoices { get; }
    WabbajackLoadoutChoice? SelectedLoadout { get; }

    /// <summary>Picks the loadout to install into. The "Create new loadout" entry only records the choice; Install creates it.</summary>
    ReactiveCommand<WabbajackLoadoutChoice, Unit> SelectLoadoutCommand { get; }

    IReadOnlyList<WabbajackArchiveRow> Archives { get; }

    /// <summary>What is still to get, or that every archive is available.</summary>
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

    /// <summary>Why Install is off, in a sentence for the page; empty while it is allowed.</summary>
    string InstallBlockedReason { get; }

    /// <summary>Why Download is off, in a sentence for the page; empty while it is allowed.</summary>
    string DownloadBlockedReason { get; }

    /// <summary>Why "Open next five" is off, in a sentence for the page; empty while it is allowed.</summary>
    string OpenNextFiveBlockedReason { get; }

    /// <summary>The list is being read or planned against the library and the game folder, which can take a while for a big list.</summary>
    bool IsPlanning { get; }

    /// <summary>What the check of the list is doing right now with its count, or empty while no check runs.</summary>
    string CheckText { get; }

    /// <summary>How far through its own phase the check is, 0 to 1.</summary>
    double CheckFraction { get; }

    /// <summary>The phase has no count, so the bar only shows that work is going on.</summary>
    bool IsCheckIndeterminate { get; }

    /// <summary>At least one archive can be fetched by the app or through the browser, and no download is running.</summary>
    bool CanDownload { get; }
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

    /// <summary>Downloads the archives the app can fetch itself and starts expecting the ones the user fetches in the browser.</summary>
    ReactiveCommand<Unit, Unit> DownloadCommand { get; }

    ReactiveCommand<WabbajackArchiveRow, Unit> OpenPageCommand { get; }

    /// <summary>Installs a held archive into the selected loadout as a mod of its own.</summary>
    ReactiveCommand<WabbajackArchiveRow, Unit> InstallArchiveCommand { get; }

    /// <summary>Opens the pages of the next five Nexus Mods files that still need a download in the browser.</summary>
    ReactiveCommand<Unit, Unit> OpenNextFiveCommand { get; }

    /// <summary>Notice finished browser downloads in the Downloads folder and add them to the library.</summary>
    bool PickUpFromDownloads { get; set; }

    /// <summary>At least one archive waits for a download in the browser, so the pickup switch has something to act on.</summary>
    bool HasWaitingArchives { get; }

    /// <summary>"Watching /home/you/Downloads" or "Pickup is off".</summary>
    string PickUpTooltip { get; }

    ReactiveCommand<Unit, Unit> ChooseDownloadsFolderCommand { get; }
    ReactiveCommand<Unit, Unit> UseDefaultDownloadsFolderCommand { get; }

    ReactiveCommand<Unit, Unit> CheckInboxCommand { get; }
    ReactiveCommand<Unit, Unit> OpenInboxFolderCommand { get; }
    ReactiveCommand<Unit, Unit> OpenHealthCheckCommand { get; }

    /// <summary>Removes the list's mods from the loadout.</summary>
    ReactiveCommand<Unit, Unit> RemoveCommand { get; }

    /// <summary>Copies files the user dropped on the page into the inbox and checks it.</summary>
    Task AddDroppedFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);
}
