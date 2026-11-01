using System.Text.RegularExpressions;
using System.Web;
using NexusMods.Paths;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// One file host the app downloads from. It says whether it can fetch an address, judged from the address alone
/// without a request, and starts the host's job for one it can. A list can name a host's file through its own
/// downloader state or, commonly, through the Manual downloader, so both go through here.
/// </summary>
public interface IHostDownloader
{
    /// <summary>The host's name as the user knows it.</summary>
    string Name { get; }

    /// <summary>Whether <paramref name="url"/> has the shape of a file this host's job can resolve. A false is not a promise that the fetch works, only that it is worth trying.</summary>
    bool CanFetch(Uri url);

    /// <summary>Starts the host's job for <paramref name="url"/>, which <see cref="CanFetch"/> accepted.</summary>
    IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination);
}

/// <summary>The hosts the app downloads from.</summary>
public static class HostDownloaders
{
    public static IHostDownloader MediaFire { get; } = new MediaFireHost();
    public static IHostDownloader ModDb { get; } = new ModDbHost();
    public static IHostDownloader Mega { get; } = new MegaHost();
    public static IHostDownloader GoogleDrive { get; } = new GoogleDriveHost();

    public static IReadOnlyList<IHostDownloader> All { get; } = [MediaFire, ModDb, Mega, GoogleDrive];

    /// <summary>The host that can fetch <paramref name="url"/>, or null when none can and the user has to.</summary>
    public static IHostDownloader? For(Uri url) => All.FirstOrDefault(host => host.CanFetch(url));

    private static string[] Segments(Uri url) => url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

    private sealed class MediaFireHost : IHostDownloader
    {
        public string Name => "MediaFire";

        // A file page (/file/<id>/<name>), or the file itself on one of the numbered download hosts. Folders and
        // the bare ?<key> form are not files this job can pick out.
        public bool CanFetch(Uri url)
        {
            if (!HostLinks.IsOn(url, "mediafire.com")) return false;
            var segments = Segments(url);
            if (Regex.IsMatch(url.Host, @"^download\d*\.mediafire\.com$", RegexOptions.IgnoreCase)) return segments.Length >= 2;
            return segments.Length >= 2 && segments[0] is "file" or "file_premium" or "download";
        }

        public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
            => MediaFireDownloadJob.Create(provider, url, fileName, destination);
    }

    private sealed class ModDbHost : IHostDownloader
    {
        public string Name => "ModDB";

        // The pages the job follows to a mirror: a download page, a mirror itself, an addon page, and a
        // mod's or game's own download or addon entry. A mod's front page or a news post offers no file.
        public bool CanFetch(Uri url)
        {
            if (!HostLinks.IsOn(url, "moddb.com")) return false;
            var segments = Segments(url);
            if (segments is ["downloads", "start" or "mirror", ..]) return true;
            if (segments is ["addons", _]) return true;
            return segments.Length >= 4 && segments[2] is "downloads" or "addons";
        }

        public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
            => ModDbDownloadJob.Create(provider, url, fileName, destination);
    }

    private sealed class MegaHost : IHostDownloader
    {
        public string Name => "MEGA";

        public bool CanFetch(Uri url) => MegaLink.TryParse(url, out _);

        public IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
            => MegaDownloadJob.Create(provider, url, fileName, destination);
    }

    private sealed class GoogleDriveHost : IHostDownloader
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

            var segments = Segments(url);
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
}
