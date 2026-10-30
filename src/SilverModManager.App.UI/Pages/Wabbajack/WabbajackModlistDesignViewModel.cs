using System.Reactive;
using System.Reactive.Linq;
using Avalonia.Media.Imaging;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using SilverModManager.Sdk.Library;
using ReactiveUI;

namespace SilverModManager.App.UI.Pages.Wabbajack;

public class WabbajackModlistDesignViewModel : APageViewModel<IWabbajackModlistViewModel>, IWabbajackModlistViewModel
{
    public LibraryFileId FileId { get; set; }
    public SilverModManager.Sdk.Loadouts.LoadoutId? PreferredLoadout { get; set; }
    public string Name => "Example list";
    public string Author => "Someone";
    public string Version => "1.0.0";
    public string GameName => "SkyrimSpecialEdition";
    public string ArchiveCountText { get; init; } = "312 archives";
    public string ArchiveSizeText { get; init; } = "48.2 GB";
    public string Description { get; init; } = "";
    public Uri? ReadmeUri { get; init; }
    public ReactiveCommand<Unit, Unit> OpenReadmeCommand { get; } = ReactiveCommand.Create(() => { });

    // Computed from the same inputs the real view model feeds the rule, so a state set here shows what the page would.
    public WabbajackStatus Status => WabbajackModlistText.Status(
        ErrorText, ProgressText, ProgressFraction, CheckText, CheckFraction, IsCheckIndeterminate, null,
        InstallBlockedReason, DownloadBlockedReason, AcquireText);
    public Bitmap? Banner => null;
    public IReadOnlyList<WabbajackLoadoutChoice> LoadoutChoices { get; } = [new(null, "Create new loadout")];
    public WabbajackLoadoutChoice? SelectedLoadout => null;
    public ReactiveCommand<WabbajackLoadoutChoice, Unit> SelectLoadoutCommand { get; } = ReactiveCommand.Create<WabbajackLoadoutChoice>(_ => { });
    public IReadOnlyList<WabbajackArchiveRow> Archives { get; init; } = [];
    public string AcquireText { get; init; } = "3 of 312 archives still to get";
    public bool PickUpFromDownloads { get; set; } = true;
    public bool HasWaitingArchives { get; init; } = true;
    public string PickUpTooltip { get; init; } = "Watching /home/you/Downloads";
    public ReactiveCommand<Unit, Unit> ChooseDownloadsFolderCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> UseDefaultDownloadsFolderCommand { get; } = ReactiveCommand.Create(() => { });
    public string InboxText { get; init; } = "In the inbox but not part of this list: wrong.zip";
    public string ExtractedSizeText => "120 GB";
    public string FreeSpaceText { get; init; } = "Extracted size: at least 120 GB, 80 GB free";
    public bool IsFreeSpaceWarning { get; init; } = true;
    public IReadOnlyList<WabbajackGameFileChoice> GameFilesReplaced => [];
    public bool SkipMissingArchives { get; set; }
    public bool CanInstall { get; init; } = false;
    public string InstallBlockedReason { get; init; } = "3 of 312 archives are not in your library yet.";
    public string DownloadBlockedReason { get; init; } = "";
    public string OpenNextFiveBlockedReason { get; init; } = "Press Download first.";
    public bool IsPlanning { get; init; } = false;
    public string CheckText { get; init; } = "";
    public double CheckFraction { get; init; } = 0;
    public bool IsCheckIndeterminate { get; init; } = false;
    public bool CanDownload { get; init; } = true;
    public bool IsInstalling { get; init; } = false;
    public string ProgressText { get; init; } = "";
    public double ProgressFraction { get; init; } = 0;
    public string ErrorText { get; init; } = "";
    public bool CanResume { get; init; } = false;
    public bool IsInstalled { get; init; } = false;
    public IReadOnlyList<WabbajackSkippedRow> SkippedFiles => [];
    // Gated like the real view model's commands, so a fixture shows a button greyed out when the page would.
    // Built on first use: the state is set by initializers that run after the constructor.
    public ReactiveCommand<Unit, Unit> DownloadCommand => _download ??= ReactiveCommand.Create(() => { }, Observable.Return(CanDownload));
    public ReactiveCommand<Unit, Unit> InstallCommand => _install ??= ReactiveCommand.Create(() => { }, Observable.Return(CanInstall));
    private ReactiveCommand<Unit, Unit>? _download, _install, _openNextFive;
    public ReactiveCommand<WabbajackArchiveRow, Unit> OpenPageCommand { get; } = ReactiveCommand.Create<WabbajackArchiveRow>(_ => { });
    public ReactiveCommand<WabbajackArchiveRow, Unit> InstallArchiveCommand { get; } = ReactiveCommand.Create<WabbajackArchiveRow>(_ => { });
    public ReactiveCommand<Unit, Unit> OpenNextFiveCommand => _openNextFive ??= ReactiveCommand.Create(() => { }, Observable.Return(OpenNextFiveBlockedReason == ""));
    public ReactiveCommand<Unit, Unit> CheckInboxCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> OpenInboxFolderCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> OpenHealthCheckCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; } = ReactiveCommand.Create(() => { });
    public Task AddDroppedFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => Task.CompletedTask;

    public WabbajackModlistDesignViewModel() : base(new DesignWindowManager()) { }
}
