using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Downloads a ModDB file. A list gives the addon or download page; the file itself is behind a mirror address
/// (<c>/downloads/mirror/...</c>) that the page, or the <c>/downloads/start/...</c> page it links to, offers.
/// </summary>
public sealed record ModDbDownloadJob : HostedDownloadJob, IJobDefinitionWithStart<ModDbDownloadJob, AbsolutePath>
{
    private const string MirrorPath = "/downloads/mirror/";
    private const string StartPath = "/downloads/start/";
    private const int MaxPages = 3;

    protected override string HostName => "ModDB";

    public static IJobTask<ModDbDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
    {
        var job = new ModDbDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<ModDbDownloadJob>>(),
            Throttle = provider.GetRequiredService<HostThrottle>(),
            Url = url,
            FileName = fileName,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<ModDbDownloadJob, AbsolutePath>(job);
    }

    public ValueTask<AbsolutePath> StartAsync(IJobContext<ModDbDownloadJob> context) => RunAsync(context);

    protected override async Task Fetch(HostTransfer transfer, CancellationToken token)
    {
        var page = Url;
        var mirror = page.AbsolutePath.StartsWith(MirrorPath, StringComparison.Ordinal) ? page : null;
        for (var pages = 0; mirror is null; pages++)
        {
            if (pages == MaxPages) throw NoMirror();
            var html = await transfer.GetPage(page, token);
            mirror = FindLink(page, html, MirrorPath);
            if (mirror is not null) break;
            page = FindLink(page, html, StartPath) ?? throw NoMirror();
        }

        using var file = await transfer.Send(HostLinks.Get(mirror, page), token);
        await transfer.Save(file, hostName: HostName);
    }

    private static Uri? FindLink(Uri page, string html, string pathPrefix)
        => HtmlTags.Find(html, "a")
            .Select(a => a.TryGetValue("href", out var href) ? HostLinks.Resolve(page, href, "moddb.com") : null)
            .FirstOrDefault(link => link is not null && link.AbsolutePath.StartsWith(pathPrefix, StringComparison.Ordinal));

    private static HostRefusedException NoMirror() => new("Could not find a ModDB mirror link on the page; download it in your browser");
}
