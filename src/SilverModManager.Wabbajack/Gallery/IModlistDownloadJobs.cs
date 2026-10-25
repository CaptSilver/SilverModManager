using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.Wabbajack.Gallery;

/// <summary>Starts the download of a <c>.wabbajack</c> file named by a gallery entry.</summary>
public interface IModlistDownloadJobs
{
    IJobTask<IDownloadJob, AbsolutePath> Create(Uri url, AbsolutePath destination);
}

/// <summary>
/// Lists are hosted on the Wabbajack CDN, which serves a file in parts and answers a plain GET with an HTML page,
/// or on any plain https host.
/// </summary>
public sealed class ModlistDownloadJobs(IServiceProvider provider) : IModlistDownloadJobs
{
    public IJobTask<IDownloadJob, AbsolutePath> Create(Uri url, AbsolutePath destination)
        => IsWabbajackCdn(url)
            ? WabbajackCdnDownloadJob.Create(provider, url, destination)
            : HttpDownloadJob.Create(provider, url, url, destination);

    internal static bool IsWabbajackCdn(Uri url)
        => WabbajackCdnDownloadJob.DomainRemaps.ContainsKey(url.Host)
           || WabbajackCdnDownloadJob.DomainRemaps.Values.Contains(url.Host);
}
