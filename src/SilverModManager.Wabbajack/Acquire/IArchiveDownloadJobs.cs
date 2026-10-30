using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.NexusWebApi;
using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.Networking.Forums;
using SilverModManager.Networking.Forums.Downloads;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.NexusWebApi;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.NexusModsApi;
using SilverModManager.Wabbajack.Acquire.Hosts;
using SilverModManager.Wabbajack.Format;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>Starts the download job that fetches one archive of a plan.</summary>
public interface IArchiveDownloadJobs
{
    /// <summary>
    /// The job for an <see cref="ArchiveState.Automatic"/> row, already running and writing to <paramref name="destination"/>.
    /// </summary>
    Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken);
}

public sealed class ArchiveDownloadJobs(IServiceProvider provider, NexusModsLibrary nexusModsLibrary, IGameDomainToGameIdMappingCache mappingCache, ForumFilesClient forumFiles) : IArchiveDownloadJobs
{
    public async Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken)
    {
        switch (row.Archive.State)
        {
            case HttpState { Headers.Length: > 0 } http:
                return HeaderedHttpDownloadJob.Create(provider, http.Url, http.Headers, row.Archive.Name, destination);

            case HttpState http:
                return HttpDownloadJob.Create(provider, http.Url, new Uri(http.Url.GetLeftPart(UriPartial.Path)), destination);

            case WabbajackCdnState cdn:
                return WabbajackCdnDownloadJob.Create(provider, cdn.Url, destination);

            case MegaState mega:
                return MegaDownloadJob.Create(provider, mega.Url, row.Archive.Name, destination);

            case GoogleDriveState drive:
                return GoogleDriveDownloadJob.Create(provider, drive.Id, row.Archive.Name, destination);

            case MediaFireState mediaFire:
                return MediaFireDownloadJob.Create(provider, mediaFire.Url, row.Archive.Name, destination);

            case ModDbState modDb:
                return ModDbDownloadJob.Create(provider, modDb.Url, row.Archive.Name, destination);

            case LoversLabState { IsAttachment: false, FileId: { } id, FileName: { } name } loversLab:
                return await ForumDownload(ForumSite.LoversLab, loversLab.Url, id, name, row, destination, cancellationToken);

            case VectorPlexusState { IsAttachment: false, FileId: { } id, FileName: { } name } vectorPlexus:
                return await ForumDownload(ForumSite.VectorPlexus, vectorPlexus.Url, id, name, row, destination, cancellationToken);

            case NexusState nexus:
                var domain = WabbajackGames.NexusDomainFor(nexus.GameName)
                    ?? throw new InvalidOperationException($"The list's game '{nexus.GameName}' has no Nexus Mods domain here");
                return await nexusModsLibrary.CreateDownloadJob(
                    destination,
                    mappingCache[domain],
                    ModId.From((uint)nexus.ModId),
                    FileId.From((uint)nexus.FileId),
                    cancellationToken: cancellationToken);

            default:
                throw new InvalidOperationException($"{row.Archive.State.GetType().Name} archives are not downloaded automatically");
        }
    }

    /// <summary>
    /// Looks the file up in the forum's files API, then fetches it from the address listed there. The lookup
    /// checks name and size first, so a wrong file fails here, before a byte is requested.
    /// </summary>
    private async Task<IJobTask<IDownloadJob, AbsolutePath>> ForumDownload(
        ForumSite site, Uri? listPage, long fileId, string fileName, ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken)
    {
        var fileUrl = await forumFiles.Resolve(site, fileId, fileName, row.Archive.Size, cancellationToken);
        return ForumDownloadJob.Create(provider, site, WithoutQuery(listPage ?? fileUrl), fileUrl, row.Archive.Size, fileName, destination);
    }

    // HostedDownloadJob logs and stores this address as the download page. The list's page address can carry a
    // csrf key and the listed file address a signing key, so neither goes in with its query.
    private static Uri WithoutQuery(Uri uri) => new(uri.GetLeftPart(UriPartial.Path));
}
