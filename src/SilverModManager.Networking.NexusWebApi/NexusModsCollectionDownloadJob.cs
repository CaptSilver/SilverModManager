using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Networking.NexusWebApi;

/// <summary>
/// A Nexus Mods collection download job.
/// </summary>
public class NexusModsCollectionDownloadJob : IJobDefinitionWithStart<NexusModsCollectionDownloadJob, AbsolutePath>, IDownloadJob
{
    /// <summary>
    /// The download destination.
    /// </summary>
    public required AbsolutePath Destination { get; set; }

    /// <summary>
    /// The collection slug
    /// </summary>
    public required CollectionSlug Slug { get; set; }
    
    /// <summary>
    /// The revision number of the collection.
    /// </summary>
    public required RevisionNumber Revision { get; set; }
    
    /// <summary>
    /// Database connection.
    /// </summary>
    public required IConnection Connection { get; init; }
    
    /// <summary>
    /// The HTTP download job.
    /// </summary>
    public IJobTask<HttpDownloadJob, AbsolutePath>? DownloadJob { get; set; }
    
    /// <summary>
    /// Nexus Mods API client.
    /// </summary>
    public required INexusApiClient ApiClient { get; set; }


    /// <summary>
    /// The Nexus Mods library.
    /// </summary>
    public required NexusModsLibrary Library { get; set; }

    /// <summary>
    /// Service provider.
    /// </summary>
    public required IServiceProvider ServiceProvider { get; set; }
    
    
    /// <summary>
    /// Creates a new <see cref="NexusModsCollectionDownloadJob"/> that downloads a collection file
    /// </summary>
    public static IJobTask<NexusModsCollectionDownloadJob, AbsolutePath> Create(IServiceProvider provider, CollectionSlug slug, RevisionNumber number, AbsolutePath destination)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new NexusModsCollectionDownloadJob
        {
            Slug = slug,
            Revision = number,
            Connection = provider.GetRequiredService<IConnection>(),
            Library = provider.GetRequiredService<NexusModsLibrary>(),
            ApiClient = provider.GetRequiredService<INexusApiClient>(),
            Destination = destination,
            ServiceProvider = provider,
        };
        return monitor.Begin<NexusModsCollectionDownloadJob, AbsolutePath>(job);
    }

    /// <inheritdoc />
    public ValueTask AddMetadata(ITransaction tx, LibraryFile.New libraryFile)
    {
        _ = new NexusModsCollectionLibraryFile.New(tx, libraryFile.Id)
        {
            CollectionSlug = Slug,
            CollectionRevisionNumber = Revision,
            LibraryFile = libraryFile,
        };
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask<AbsolutePath> StartAsync(IJobContext<NexusModsCollectionDownloadJob> context)
    {
        var downloadLinks = await ApiClient.CollectionDownloadLinksAsync(Slug, Revision, true, context.CancellationToken);
        var links = downloadLinks.Data.DownloadLinks;
        if (links.Length == 0)
            throw new InvalidOperationException("Nexus Mods returned no download links for this collection revision");

        var logger = ServiceProvider.GetRequiredService<ILogger<NexusModsCollectionDownloadJob>>();
        var failures = new List<string>();

        // The links are different CDN nodes for the same file, and a node can be unreachable from one network while
        // the next works fine, so try them in the order Nexus gave until one delivers.
        foreach (var link in links)
        {
            var host = link.Uri.Authority;
            try
            {
                // The page URI only ends up in logs, and the link's query holds a signature.
                DownloadJob = HttpDownloadJob.Create(ServiceProvider, link.Uri, new Uri(link.Uri.GetLeftPart(UriPartial.Path)), Destination);
                return await DownloadJob;
            }
            catch (Exception e) when (!context.CancellationToken.IsCancellationRequested && e is not OperationCanceledException)
            {
                var reason = Describe(host, e);
                failures.Add(reason);
                logger.LogInformation("Collection download from `{Cdn}` failed ({Reason}), moving on to the next link", host, reason);
            }
        }

        throw new HttpRequestException($"None of the download servers for this collection worked: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// A reason that names the host but never carries the URL, whose query holds a signature.
    /// </summary>
    private static string Describe(string host, Exception exception) => exception switch
    {
        HostNotRespondingException e => e.Message,
        HttpRequestException { StatusCode: { } status } => $"{host} returned {(int)status}",
        _ => $"{host} failed to download",
    };
}
