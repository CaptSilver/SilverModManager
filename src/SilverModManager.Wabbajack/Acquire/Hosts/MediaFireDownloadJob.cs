using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Downloads a MediaFire file. The address in a list is the file's page; the real link sits on its download button,
/// either as the href or, on newer pages, base64 in <c>data-scrambled-url</c>.
/// </summary>
public sealed record MediaFireDownloadJob : HostedDownloadJob, IJobDefinitionWithStart<MediaFireDownloadJob, AbsolutePath>
{
    protected override string HostName => "MediaFire";

    public static IJobTask<MediaFireDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string fileName, AbsolutePath destination)
    {
        var job = new MediaFireDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<MediaFireDownloadJob>>(),
            Throttle = provider.GetRequiredService<HostThrottle>(),
            Url = url,
            FileName = fileName,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<MediaFireDownloadJob, AbsolutePath>(job);
    }

    public ValueTask<AbsolutePath> StartAsync(IJobContext<MediaFireDownloadJob> context) => RunAsync(context);

    protected override async Task Fetch(HostTransfer transfer, CancellationToken token)
    {
        using var page = await transfer.Send(HostLinks.Get(Url), token);
        transfer.EnsureSuccess(page);
        // An address that already points at the file needs no button.
        if (!HostTransfer.IsWebPage(page))
        {
            await transfer.Save(page, hostName: HostName);
            return;
        }

        var html = await page.Content.ReadAsStringAsync(token);
        var link = FindLink(Url, html) ?? throw Explain(html);
        using var file = await transfer.Send(HostLinks.Get(link, Url), token);
        await transfer.Save(file, hostName: HostName);
    }

    private static Uri? FindLink(Uri page, string html)
    {
        foreach (var anchor in HtmlTags.Find(html, "a"))
        {
            var isButton = (anchor.TryGetValue("id", out var id) && id == "downloadButton")
                           || (anchor.TryGetValue("aria-label", out var label) && label.StartsWith("Download", StringComparison.OrdinalIgnoreCase));
            if (!isButton) continue;

            // The scrambled address is the real one; the href beside it is a placeholder such as "#".
            if (anchor.TryGetValue("data-scrambled-url", out var scrambled) && Unscramble(scrambled) is { } decoded && HostLinks.Resolve(page, decoded, "mediafire.com") is { } hidden) return hidden;
            if (anchor.TryGetValue("href", out var href) && !href.StartsWith('#') && HostLinks.Resolve(page, href, "mediafire.com") is { } direct) return direct;
        }
        return null;
    }

    private static string? Unscramble(string text)
    {
        var buffer = new byte[text.Length];
        return Convert.TryFromBase64String(text, buffer, out var written) ? Encoding.UTF8.GetString(buffer, 0, written) : null;
    }

    private static HostRefusedException Explain(string html)
        => html.Contains("Invalid or Deleted File", StringComparison.OrdinalIgnoreCase) || html.Contains("has been removed", StringComparison.OrdinalIgnoreCase)
            ? new HostRefusedException("MediaFire says this file was removed; there is nothing to download")
            : new HostRefusedException("Could not find MediaFire's download link on the page; download it in your browser");
}
