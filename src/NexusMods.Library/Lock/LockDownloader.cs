using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Abstractions.NexusWebApi.Types;
using NexusMods.Networking.NexusWebApi;
using NexusMods.Paths;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Library.Lock;

/// <summary>
/// Fetches lock entries with the app's own download paths: Nexus Mods files by file id like collection downloads do,
/// release assets through <see cref="AddGitHubReleaseAssetJob"/>. Services are resolved when first used so a host
/// without Nexus Mods or GitHub support can still list and verify a lock.
/// </summary>
internal sealed class LockDownloader(IServiceProvider provider) : ILockDownloader
{
    public async ValueTask<LibraryFile.ReadOnly> DownloadNexusFile(FileId fileId, NexusModsGameId game, CancellationToken cancellationToken)
    {
        var nexusModsLibrary = provider.GetRequiredService<NexusModsLibrary>();
        await using var destination = provider.GetRequiredService<TemporaryFileManager>().CreateFile();

        var job = await nexusModsLibrary.CreateDownloadJob(destination, game, fileId, cancellationToken: cancellationToken);
        return await provider.GetRequiredService<ILibraryService>().AddDownload(job);
    }

    public async ValueTask<LibraryFile.ReadOnly> DownloadGitHubAsset(GitHubRepositoryRef repository, string tag, string asset, Sha256Value expected, CancellationToken cancellationToken)
    {
        // The whole name is the suffix, so only that exact asset can be picked.
        var job = AddGitHubReleaseAssetJob.Create(provider, repository, tag, suffix: asset, expectedSha256: expected);
        var release = await job;
        return release.AsLibraryFile();
    }

    public async ValueTask Discard(LibraryFile.ReadOnly file)
    {
        await provider.GetRequiredService<ILibraryService>().RemoveLibraryItems([file.AsLibraryItem()]);
    }
}
