using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Abstractions.Downloads;

/// <summary>
/// One file host the app downloads from. It says whether it can fetch an address, judged from the address alone
/// without a request, and starts the host's job for one it can. A Wabbajack list can name a host's file through its own
/// downloader state or, commonly, through the Manual downloader, and a pasted link names it directly, so all of them
/// go through here.
/// </summary>
public interface IUrlDownloader
{
    /// <summary>The host's name as the user knows it.</summary>
    string Name { get; }

    /// <summary>Whether <paramref name="url"/> has the shape of a file this host's job can resolve. A false is not a promise that the fetch works, only that it is worth trying.</summary>
    bool CanFetch(Uri url);

    /// <summary>Starts the host's job for <paramref name="url"/>, which <see cref="CanFetch"/> accepted.</summary>
    IJobTask<IDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination);
}
