using System.Reactive;
using Avalonia.Media.Imaging;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.Sdk.Library;
using ReactiveUI;

namespace NexusMods.App.UI.Pages.Wabbajack;

public class WabbajackModlistDesignViewModel : APageViewModel<IWabbajackModlistViewModel>, IWabbajackModlistViewModel
{
    public LibraryFileId FileId { get; set; }
    public string Name => "Example list";
    public string Author => "Someone";
    public string Version => "1.0.0";
    public string GameName => "SkyrimSpecialEdition";
    public string ArchiveSummary => "312 archives, 48.2 GB";
    public Bitmap? Banner => null;
    public IReadOnlyList<WabbajackLoadoutChoice> LoadoutChoices { get; } = [new(null, "Create new loadout")];
    public WabbajackLoadoutChoice? SelectedLoadout => null;
    public ReactiveCommand<WabbajackLoadoutChoice, Unit> SelectLoadoutCommand { get; } = ReactiveCommand.Create<WabbajackLoadoutChoice>(_ => { });
    public IReadOnlyList<WabbajackArchiveRow> Archives => [];
    public string AcquireText => "3 of 312 archives still to get";
    public string InboxText => "In the inbox but not part of this list: wrong.zip";
    public string ExtractedSizeText => "120 GB";
    public string FreeSpaceText => "Extracted size: at least 120 GB, 80 GB free";
    public bool IsFreeSpaceWarning => true;
    public IReadOnlyList<WabbajackGameFileChoice> GameFilesReplaced => [];
    public bool SkipMissingArchives { get; set; }
    public bool CanInstall => false;
    public bool CanDownload => true;
    public bool IsInstalling => false;
    public string ProgressText => "";
    public double ProgressFraction => 0;
    public string ErrorText => "";
    public bool CanResume => false;
    public bool IsInstalled => false;
    public IReadOnlyList<WabbajackSkippedRow> SkippedFiles => [];
    public ReactiveCommand<Unit, Unit> DownloadCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> InstallCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<WabbajackArchiveRow, Unit> OpenPageCommand { get; } = ReactiveCommand.Create<WabbajackArchiveRow>(_ => { });
    public ReactiveCommand<Unit, Unit> OpenNextFiveCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> CheckInboxCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> OpenInboxFolderCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> OpenHealthCheckCommand { get; } = ReactiveCommand.Create(() => { });
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; } = ReactiveCommand.Create(() => { });
    public Task AddDroppedFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken) => Task.CompletedTask;

    public WabbajackModlistDesignViewModel() : base(new DesignWindowManager()) { }
}
