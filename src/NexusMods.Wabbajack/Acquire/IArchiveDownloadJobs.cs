using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Downloads;
using NexusMods.Abstractions.NexusWebApi;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.HttpDownloader;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Wabbajack.Format;

namespace NexusMods.Wabbajack.Acquire;

/// <summary>Starts the download job that fetches one archive of a plan.</summary>
public interface IArchiveDownloadJobs
{
    /// <summary>
    /// The job for an <see cref="ArchiveState.Automatic"/> row, already running and writing to <paramref name="destination"/>.
    /// </summary>
    Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken);
}

public sealed class ArchiveDownloadJobs(IServiceProvider provider, NexusModsLibrary nexusModsLibrary, IGameDomainToGameIdMappingCache mappingCache) : IArchiveDownloadJobs
{
    public async Task<IJobTask<IDownloadJob, AbsolutePath>> Create(ArchivePlanRow row, AbsolutePath destination, CancellationToken cancellationToken)
    {
        switch (row.Archive.State)
        {
            case HttpState http:
                return HttpDownloadJob.Create(provider, http.Url, http.Url, destination);

            case WabbajackCdnState cdn:
                return WabbajackCdnDownloadJob.Create(provider, cdn.Url, destination);

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
}
