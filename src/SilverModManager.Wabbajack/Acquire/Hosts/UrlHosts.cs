using System.Text.RegularExpressions;
using System.Web;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// The file hosts the app downloads Wabbajack archives from, registered as <see cref="IUrlDownloader"/>s.
/// </summary>
internal static class UrlHosts
{
    internal static string[] Segments(Uri url) => url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>MediaFire files, fetched by <see cref="MediaFireDownloadJob"/>.</summary>
internal sealed class MediaFireHost : IUrlDownloader
{
    public string Name => "MediaFire";

    // A file page (/file/<id>/<name>), or the file itself on one of the numbered download hosts. Folders and
    // the bare ?<key> form are not files this job can pick out.
    public bool CanFetch(Uri url)
    {
        if (!HostLinks.IsOn(url, "mediafire.com")) return false;
        var segments = UrlHosts.Segments(url);
        if (Regex.IsMatch(url.Host, @"^download\d*\.mediafire\.com$", RegexOptions.IgnoreCase)) return segments.Length >= 2;
        return segments.Length >= 2 && segments[0] is "file" or "file_premium" or "download";
    }

    public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
        => MediaFireDownloadJob.Create(provider, url, fileName, destination);
}

/// <summary>ModDB files, fetched by <see cref="ModDbDownloadJob"/>.</summary>
internal sealed class ModDbHost : IUrlDownloader
{
    public string Name => "ModDB";

    // The pages the job follows to a mirror: a download page, a mirror itself, an addon page, and a
    // mod's or game's own download or addon entry. A mod's front page or a news post offers no file.
    public bool CanFetch(Uri url)
    {
        if (!HostLinks.IsOn(url, "moddb.com")) return false;
        var segments = UrlHosts.Segments(url);
        if (segments is ["downloads", "start" or "mirror", ..]) return true;
        if (segments is ["addons", _]) return true;
        return segments.Length >= 4 && segments[2] is "downloads" or "addons";
    }

    public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
        => ModDbDownloadJob.Create(provider, url, fileName, destination);
}

/// <summary>MEGA files, fetched by <see cref="MegaDownloadJob"/>.</summary>
internal sealed class MegaHost : IUrlDownloader
{
    public string Name => "MEGA";

    public bool CanFetch(Uri url) => MegaLink.TryParse(url, out _);

    public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
        => MegaDownloadJob.Create(provider, url, fileName, destination);
}

/// <summary>Google Drive files, fetched by <see cref="GoogleDriveDownloadJob"/>.</summary>
internal sealed class GoogleDriveHost : IUrlDownloader
{
    public string Name => "Google Drive";

    public bool CanFetch(Uri url) => TryGetFileId(url, out _);

    public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
        => TryGetFileId(url, out var id)
            ? GoogleDriveDownloadJob.Create(provider, id, fileName, destination)
            : throw new ArgumentException($"{HostTransfer.Plain(url)} is not a Google Drive file link", nameof(url));

    // /file/d/<id>/view, /open?id=<id>, /uc?id=<id> and the download host's /download?id=<id>. A folder, or a
    // Docs file that only Google's editor opens, has no download address for the job.
    private static bool TryGetFileId(Uri url, out string id)
    {
        id = "";
        if (!HostLinks.IsOn(url, "drive.google.com") && !HostLinks.IsOn(url, "drive.usercontent.google.com")) return false;

        var segments = UrlHosts.Segments(url);
        // Links copied while signed in to a second account carry /u/<n>/ in front.
        if (segments is ["u", _, ..]) segments = segments[2..];

        string? found = segments switch
        {
            ["file", "d", var fileId, ..] => fileId,
            ["open" or "uc" or "download", ..] => HttpUtility.ParseQueryString(url.Query)["id"],
            _ => null,
        };
        if (found is null || found.Length < 10 || found.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;

        id = found;
        return true;
    }
}
