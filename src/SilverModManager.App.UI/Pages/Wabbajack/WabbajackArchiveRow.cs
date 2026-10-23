using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.App.UI.Pages.Wabbajack;

/// <summary>One line of the archive table.</summary>
public sealed record WabbajackArchiveRow(ArchivePlanRow Plan)
{
    public string Name => Plan.Archive.Name;
    public string SizeText => Size.FromLong(Plan.Archive.Size).ToString();
    public string Source => Plan.Archive.State switch
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
        LoversLabState => "Lovers Lab",
        VectorPlexusState => "VectorPlexus",
        UnsupportedState unsupported => unsupported.TypeName,
        var other => other.GetType().Name,
    };
    public string StateText => WabbajackModlistText.State(Plan);
    public string Reason => Plan.Reason ?? "";

    /// <summary>Browser and Manual rows (and an unsupported one that names a page) can send the user to where the file is.</summary>
    public bool CanOpenPage => Plan.Page is not null && Plan.State is ArchiveState.Browser or ArchiveState.Manual or ArchiveState.Unsupported;
}
