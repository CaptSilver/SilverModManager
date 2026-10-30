using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Downloads a public Google Drive file. The download address answers a small file with the bytes; a larger one gets
/// a virus-scan page whose form, sent as is, returns the file. Anything else that is a web page (a sign-in, a quota
/// notice) fails with a reason, since only the user's own browser can get past it.
/// </summary>
public sealed record GoogleDriveDownloadJob : HostedDownloadJob, IJobDefinitionWithStart<GoogleDriveDownloadJob, AbsolutePath>
{
    public required string FileId { get; init; }

    /// <summary>The download endpoint; a field so a test can point it elsewhere.</summary>
    public Uri DownloadUrl { get; init; } = new("https://drive.usercontent.google.com/download");

    protected override string HostName => "Google Drive";

    public static IJobTask<GoogleDriveDownloadJob, AbsolutePath> Create(IServiceProvider provider, string fileId, string fileName, AbsolutePath destination)
    {
        var job = new GoogleDriveDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<GoogleDriveDownloadJob>>(),
            Throttle = provider.GetRequiredService<HostThrottle>(),
            Url = PageFor(fileId),
            FileId = fileId,
            FileName = fileName,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<GoogleDriveDownloadJob, AbsolutePath>(job);
    }

    /// <summary>The address a person opens in a browser for the file; also the download page recorded for the library file.</summary>
    public static Uri PageFor(string fileId) => new($"https://drive.google.com/uc?id={Uri.EscapeDataString(fileId)}&export=download");

    public ValueTask<AbsolutePath> StartAsync(IJobContext<GoogleDriveDownloadJob> context) => RunAsync(context);

    protected override async Task Fetch(HostTransfer transfer, CancellationToken token)
    {
        var first = new Uri($"{DownloadUrl}?id={Uri.EscapeDataString(FileId)}&export=download&confirm=t");
        using var response = await transfer.Send(HostLinks.Get(first), token);
        transfer.EnsureSuccess(response);
        if (!HostTransfer.IsWebPage(response))
        {
            await transfer.Save(response, hostName: HostName);
            return;
        }

        var html = await response.Content.ReadAsStringAsync(token);
        var form = ScanForm(first, html) ?? throw Explain(html);
        using var confirmed = await transfer.Send(HostLinks.Get(form), token);
        transfer.EnsureSuccess(confirmed);
        if (HostTransfer.IsWebPage(confirmed)) throw Explain(await confirmed.Content.ReadAsStringAsync(token));
        await transfer.Save(confirmed, hostName: HostName);
    }

    /// <summary>The address the virus-scan page's form would send the browser to, or null when the page has no such form.</summary>
    private static Uri? ScanForm(Uri page, string html)
    {
        var form = HtmlTags.Find(html, "form").FirstOrDefault(f => f.TryGetValue("id", out var id) && id == "download-form");
        if (form is null || !form.TryGetValue("action", out var action)) return null;

        var query = new StringBuilder();
        foreach (var input in HtmlTags.Find(html, "input"))
        {
            if (!input.TryGetValue("name", out var name) || !input.TryGetValue("value", out var value)) continue;
            if (input.TryGetValue("type", out var type) && !type.Equals("hidden", StringComparison.OrdinalIgnoreCase)) continue;
            query.Append(query.Length == 0 ? '?' : '&').Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        // The form is page content: it may only lead back to Google.
        var target = HostLinks.Resolve(page, action, "google.com") ?? HostLinks.Resolve(page, action, "googleusercontent.com");
        return target is null ? null : new Uri(target.GetLeftPart(UriPartial.Path) + query);
    }

    private static HostRefusedException Explain(string html)
    {
        if (html.Contains("Too many users have viewed or downloaded", StringComparison.OrdinalIgnoreCase) || html.Contains("Quota exceeded", StringComparison.OrdinalIgnoreCase))
            return new HostRefusedException("Google Drive's download limit for this file is used up; try later or download in your browser");
        if (html.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) || html.Contains("ServiceLogin", StringComparison.OrdinalIgnoreCase))
            return new HostRefusedException("Google Drive says this file is not shared publicly; sign in and download it in your browser");
        return new HostRefusedException("Google Drive sent a web page instead of the file; download it in your browser");
    }
}
