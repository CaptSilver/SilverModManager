using DynamicData.Kernel;
using NexusMods.Paths;
using ReactiveUI;
using ReactiveUI.SourceGenerators;

namespace SilverModManager.Networking.HttpDownloader;

/// <summary>
/// The size and byte count of a download whose job writes the file itself and says how far it got, so the Downloads
/// page reads it the same way as an <see cref="HttpDownloadJob"/>'s.
/// </summary>
public sealed partial class ReportedDownloadState : ReactiveObject, IHttpDownloadState
{
    [Reactive] public partial Optional<Size> ContentLength { get; set; }
    [Reactive] public partial Size TotalBytesDownloaded { get; set; }

    /// <summary>Records that <paramref name="written"/> of <paramref name="total"/> bytes are on disk.</summary>
    public void Report(long written, long? total)
    {
        if (total is { } length) ContentLength = Size.FromLong(length);
        TotalBytesDownloaded = Size.FromLong(written);
    }
}
