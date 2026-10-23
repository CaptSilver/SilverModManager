using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Networking.GitHub;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.Library;

namespace NexusMods.Library;

/// <summary>
/// <see cref="IGitHubReleaseAssetSource"/> over <see cref="AddGitHubReleaseAssetJob"/>, with the digest GitHub
/// reports required rather than trusted on first use.
/// </summary>
internal sealed class GitHubReleaseAssetSource : IGitHubReleaseAssetSource
{
    private readonly IServiceProvider _provider;
    private readonly Func<GitHubRepositoryRef, string, string?, IJobTask<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly>> _start;

    public GitHubReleaseAssetSource(IServiceProvider provider)
    {
        _provider = provider;
        _start = (repository, suffix, nameHint) => AddGitHubReleaseAssetJob.Create(provider, repository, tag: null, suffix, nameHint, requireAdvertisedDigest: true);
    }

    internal GitHubReleaseAssetSource(IServiceProvider provider, IGitHubApi api, HttpClient client)
    {
        _provider = provider;
        _start = (repository, suffix, nameHint) => AddGitHubReleaseAssetJob.Create(provider, api, client, repository, tag: null, suffix, nameHint, expectedSha256: null, requireAdvertisedDigest: true);
    }

    public async ValueTask<LibraryFile.ReadOnly> AddLatestAssetAsync(ulong repositoryId, string suffix, string? nameHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = _start(GitHubRepositoryRef.FromId(repositoryId), suffix, nameHint);

        // The job runs on the monitor's own token, so the caller's is forwarded as a cancel of the job.
        var monitor = _provider.GetRequiredService<IJobMonitor>();
        await using var registration = cancellationToken.Register(() => monitor.Cancel(job));
        var result = await job;
        return result.AsLibraryFile();
    }
}
