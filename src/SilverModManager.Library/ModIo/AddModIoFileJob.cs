using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.ModIo;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Library.ModIo;

namespace SilverModManager.Library;

/// <summary>
/// Downloads one file of a mod.io mod, checks it and adds it to the library together with where it came from. A file
/// whose bytes do not match is never added.
/// </summary>
/// <remarks>
/// mod.io lists an MD5 for every modfile, so the bytes must match it. A file fetched for a lock (which names a SHA-256)
/// must match the pinned SHA-256 as well. The library then holds both digests of the file. The download address mod.io
/// lists can expire: build the job from a listing that was just read.
/// </remarks>
[PublicAPI]
public class AddModIoFileJob : IJobDefinitionWithStart<AddModIoFileJob, ModIoLibraryFile.ReadOnly>
{
    /// <summary>mod.io's id for the game the mod belongs to.</summary>
    public required uint GameId { get; init; }

    /// <summary>The mod's id.</summary>
    public required uint ModId { get; init; }

    /// <summary>The modfile as mod.io lists it, with its download address.</summary>
    public required ModIoFile File { get; init; }

    /// <summary>The mod's page, which the download names as where it came from.</summary>
    public required Uri Page { get; init; }

    /// <summary>Where the downloaded file goes: the games it is recorded for.</summary>
    public required IAcquisitionContext Acquisition { get; init; }

    /// <summary>The mod's update stamp now, which the update check compares against later.</summary>
    public long DateUpdated { get; init; }

    /// <summary>The digest the downloaded bytes must have, if one is already pinned.</summary>
    public Sha256Value? ExpectedSha256 { get; init; }

    private HttpClient Client { get; }
    private IServiceProvider ServiceProvider { get; }

    private AddModIoFileJob(IServiceProvider serviceProvider, HttpClient client)
    {
        ServiceProvider = serviceProvider;
        Client = client;
    }

    public static IJobTask<AddModIoFileJob, ModIoLibraryFile.ReadOnly> Create(
        IServiceProvider provider, uint gameId, uint modId, ModIoFile file, Uri page, IAcquisitionContext acquisition, long dateUpdated, Sha256Value? expectedSha256 = null)
        => Create(provider, provider.GetRequiredService<HttpClient>(), gameId, modId, file, page, acquisition, dateUpdated, expectedSha256);

    internal static IJobTask<AddModIoFileJob, ModIoLibraryFile.ReadOnly> Create(
        IServiceProvider provider, HttpClient client, uint gameId, uint modId, ModIoFile file, Uri page, IAcquisitionContext acquisition, long dateUpdated, Sha256Value? expectedSha256 = null)
    {
        var job = new AddModIoFileJob(provider, client)
        {
            GameId = gameId,
            ModId = modId,
            File = file,
            Page = page,
            Acquisition = acquisition,
            DateUpdated = dateUpdated,
            ExpectedSha256 = expectedSha256,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<AddModIoFileJob, ModIoLibraryFile.ReadOnly>(job);
    }

    public async ValueTask<ModIoLibraryFile.ReadOnly> StartAsync(IJobContext<AddModIoFileJob> context)
    {
        ModIoFiles.CheckListing(File, ExpectedSha256);
        if (File.Download is not { BinaryUrl.Length: > 0 } download || !Uri.TryCreate(download.BinaryUrl, UriKind.Absolute, out var address))
            throw new ModIoIntegrityException($"mod.io lists no download address for {MarkdownText.Code(File.FileName)}.");

        var temporaryFileManager = ServiceProvider.GetRequiredService<TemporaryFileManager>();
        await using var folder = temporaryFileManager.CreateFolder();
        var downloaded = folder.Path.Combine(File.FileName);

        try
        {
            await context.YieldAsync();
            var job = new HttpDownloadJob
            {
                Uri = address,
                DownloadPageUri = Page,
                Destination = downloaded,
                Client = Client,
                Logger = ServiceProvider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            };
            var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();
            var downloading = monitor.Begin<HttpDownloadJob, AbsolutePath>(job);
            // The download is a job of its own, so cancelling this one doesn't reach it unless passed on.
            await using (context.CancellationToken.Register(() => monitor.Cancel(downloading)))
            {
                await downloading;
            }

            await context.YieldAsync();
            return await ModIoFiles.VerifyAndAdd(ServiceProvider, downloaded, File, GameId, ModId, DateUpdated, ExpectedSha256, Acquisition, context.CancellationToken);
        }
        finally
        {
            // The folder is removed on dispose; this also covers a file that outlives a failed dispose.
            if (downloaded.FileExists) downloaded.Delete();
        }
    }
}
