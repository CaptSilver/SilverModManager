using JetBrains.Annotations;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Abstractions.Downloads;

/// <summary>
/// A download the Downloads page lists: the outermost job of a transfer, whatever host it comes from. A job that
/// only moves bytes for another (the HTTP job inside a Nexus download) is not tracked, or it would be listed twice.
/// </summary>
[PublicAPI]
public interface ITrackedDownloadJob : IDownloadJob
{
    /// <summary>The name shown for the download; empty when the source has none yet, and the destination's file name is used.</summary>
    string DisplayName { get; }

    /// <summary>Where the file comes from as the user knows it, such as "Nexus Mods" or "MEGA".</summary>
    string SourceName { get; }

    /// <summary>The page the file is downloaded from, without a query, which can carry a key; null when there is none.</summary>
    Uri? PageUri { get; }

    /// <summary>What the download is part of, such as the name of a modlist; null when it stands alone.</summary>
    string? GroupName => null;

    /// <summary>
    /// The job that moves the bytes when this one hands them to another, which then reports progress and takes pause,
    /// resume and cancel. Null when this job moves them itself.
    /// </summary>
    IJobTask? Transfer => null;
}
