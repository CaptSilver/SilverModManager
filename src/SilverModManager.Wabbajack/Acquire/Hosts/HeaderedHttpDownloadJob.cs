using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// Downloads a plain address for which the modlist also names request headers, such as a <c>Referer</c> some file
/// servers insist on. Only the headers <see cref="HeaderPolicy"/> allows are sent; a list asking for any other is
/// refused before a request goes out.
/// </summary>
public sealed record HeaderedHttpDownloadJob : HostedDownloadJob, IJobDefinitionWithStart<HeaderedHttpDownloadJob, AbsolutePath>
{
    /// <summary>The list's header lines, each <c>Name: value</c>.</summary>
    public required string[] Headers { get; init; }

    protected override string HostName => Url.Host;

    public static IJobTask<HeaderedHttpDownloadJob, AbsolutePath> Create(IServiceProvider provider, Uri url, string[] headers, string fileName, AbsolutePath destination)
    {
        var job = new HeaderedHttpDownloadJob
        {
            Client = provider.GetRequiredService<HttpClient>(),
            Logger = provider.GetRequiredService<ILogger<HeaderedHttpDownloadJob>>(),
            Throttle = provider.GetRequiredService<HostThrottle>(),
            Url = url,
            Headers = headers,
            FileName = fileName,
            Destination = destination,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<HeaderedHttpDownloadJob, AbsolutePath>(job);
    }

    public ValueTask<AbsolutePath> StartAsync(IJobContext<HeaderedHttpDownloadJob> context) => RunAsync(context);

    protected override async Task Fetch(HostTransfer transfer, CancellationToken token)
    {
        if (HeaderPolicy.Refused(Headers).FirstOrDefault() is { } refused)
            throw new HostRefusedException($"The list asks for the request header {refused}, which this app does not send; download it in your browser");

        using var request = HostLinks.Get(Url);
        foreach (var line in Headers)
        {
            HeaderPolicy.TryParse(line, out var name, out var value);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await transfer.Send(request, token);
        await transfer.Save(response, hostName: HostName);
    }
}
