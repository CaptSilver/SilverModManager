using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Networking.Forums;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Fetches a file from the address a forum's files API listed for it. The request carries no Authorization: the
/// token opens the API, and the listed address is what the forum hands out for the file itself.
/// </summary>
/// <remarks>
/// <see cref="HostedDownloadJob.Url"/> is the forum page the file belongs to, query-free, because that is what the
/// base class records and logs as the download page. The listed address is <see cref="FileUrl"/> and is never
/// logged or stored, since it may carry a signing key.
/// </remarks>
public sealed record ForumDownloadJob : HostedDownloadJob, IJobDefinitionWithStart<ForumDownloadJob, AbsolutePath>
{
    /// <summary>The address the files API lists for the file.</summary>
    public required Uri FileUrl { get; init; }

    /// <summary>The size the list expects; the transfer fails on any other.</summary>
    public required long Size { get; init; }

    public required ForumSite Site { get; init; }

    protected override string HostName => Site.DisplayName();

    public static IJobTask<ForumDownloadJob, AbsolutePath> Create(
        IServiceProvider provider, ForumSite site, Uri page, Uri fileUrl, long size, string fileName, AbsolutePath destination)
    {
        var job = new ForumDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<ForumDownloadJob>>(),
            Throttle = provider.GetRequiredService<HostThrottle>(),
            Url = page,
            FileUrl = fileUrl,
            Size = size,
            Site = site,
            FileName = fileName,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<ForumDownloadJob, AbsolutePath>(job);
    }

    public ValueTask<AbsolutePath> StartAsync(IJobContext<ForumDownloadJob> context) => RunAsync(context);

    protected override async Task Fetch(HostTransfer transfer, CancellationToken token)
    {
        using var request = HostLinks.Get(FileUrl, Url);
        using var response = await transfer.Send(request, token);
        await transfer.Save(response, expectedSize: Size, hostName: HostName);
    }
}
