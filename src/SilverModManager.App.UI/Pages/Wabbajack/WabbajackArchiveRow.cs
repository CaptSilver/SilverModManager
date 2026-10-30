using NexusMods.Paths;
using ReactiveUI;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>
/// One line of the archive table. The line stays the same object while its archive's plan row changes, so a download
/// finishing touches that one line and not the whole table.
/// </summary>
public sealed class WabbajackArchiveRow(ArchivePlanRow plan) : ReactiveObject
{
    private ArchivePlanRow _plan = plan;

    public ArchivePlanRow Plan => _plan;

    // The archive entry never changes for a row; only its state, reason and page do.
    public string Name { get; } = plan.Archive.Name;
    public string SizeText { get; } = Size.FromLong(plan.Archive.Size).ToString();
    public string Source { get; } = SourceOf(plan);

    private static string SourceOf(ArchivePlanRow plan) => plan.Archive.State switch
    {
        NexusState => "Nexus Mods",
        HttpState http => http.Url.Host,
        WabbajackCdnState => "Wabbajack CDN",
        GameFileSourceState => "Game files",
        ManualState manual => manual.Url.Host,
        MegaState => "Mega",
        GoogleDriveState => "Google Drive",
        MediaFireState => "MediaFire",
        ModDbState => "ModDB",
        LoversLabState => "LoversLab",
        VectorPlexusState => "VectorPlexus",
        UnsupportedState unsupported => unsupported.TypeName,
        var other => other.GetType().Name,
    };

    public string StateText => WabbajackModlistText.State(Plan);

    /// <summary>Why the row is in its state, then what is worth knowing about the archive; either may be missing.</summary>
    public string Reason => string.Join("; ", new[] { Plan.Reason, Plan.Note }.Where(text => !string.IsNullOrEmpty(text)));

    /// <summary>A held archive can be installed on its own, as a mod of the loadout.</summary>
    public bool CanInstall => CanInstallOf(Plan);

    private static bool CanInstallOf(ArchivePlanRow plan) => plan.State == ArchiveState.Have && plan.Library is not null;

    /// <summary>Browser and Manual rows (and an unsupported one that names a page) can send the user to where the file is.</summary>
    public bool CanOpenPage => CanOpenPageOf(Plan);

    private static bool CanOpenPageOf(ArchivePlanRow plan) => plan.Page is not null && plan.State is ArchiveState.Browser or ArchiveState.Manual or ArchiveState.Unsupported;

    /// <summary>Takes the archive's new plan row, telling only what its change touches.</summary>
    public void Update(ArchivePlanRow plan)
    {
        var old = _plan;
        if (old.Equals(plan)) return;
        _plan = plan;
        this.RaisePropertyChanged(nameof(Plan));
        if (old.State != plan.State || old.Opened != plan.Opened) this.RaisePropertyChanged(nameof(StateText));
        if (old.Reason != plan.Reason || old.Note != plan.Note) this.RaisePropertyChanged(nameof(Reason));
        if (CanInstallOf(old) != CanInstall) this.RaisePropertyChanged(nameof(CanInstall));
        if (CanOpenPageOf(old) != CanOpenPage) this.RaisePropertyChanged(nameof(CanOpenPage));
    }
}
