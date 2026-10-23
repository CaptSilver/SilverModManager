using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Library.Lock;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;

namespace NexusMods.Library.Sources;

/// <summary>
/// GitHub release assets, fetched through <see cref="AddGitHubReleaseAssetJob"/>. Services are resolved when first
/// used so a host without GitHub support can still list, export and verify a lock.
/// </summary>
internal sealed class GitHubLibrarySource(IServiceProvider provider) : ILibrarySource
{
    public string Name => LockSources.GitHub;

    public ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        if (!file.TryGetAsGitHubReleaseLibraryFile(out var release)) return ValueTask.FromResult<LibrarySourceEntry?>(null);

        var entry = new LibrarySourceEntry(LockExport.SanitizeId(groupName, file.FileName.ToString()), release.Tag, null, release.AssetName, release.PinnedSha256.ToString(), Name);
        return ValueTask.FromResult<LibrarySourceEntry?>(entry);
    }

    public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context)
    {
        return FindRepository(context.Connection, entry.Asset) is null
            ? $"{entry.Id}: no repository is known for {entry.Asset}; add one release of it to the library once so its repository is recorded."
            : null;
    }

    public async ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        var repository = FindRepository(context.Connection, entry.Asset)
            ?? throw new InvalidOperationException($"{entry.Id}: no repository is known for {entry.Asset}.");

        // The whole name is the suffix, so only that exact asset can be picked.
        var job = AddGitHubReleaseAssetJob.Create(provider, repository, entry.Version, suffix: entry.Asset, expectedSha256: expected);
        var release = await job;
        return release.AsLibraryFile();
    }

    // The lock names no repository, so a library file that was downloaded from a release under the same asset name is
    // the only place the importer can learn it from.
    internal static GitHubRepositoryRef? FindRepository(IConnection connection, string asset)
    {
        foreach (var file in GitHubReleaseLibraryFile.All(connection.Db))
        {
            if (file.AssetName != asset) continue;
            if (GitHubReleaseLibraryFile.RepositoryId.TryGetValue(file, out var id)) return GitHubRepositoryRef.FromId(id);
            if (GitHubReleaseLibraryFile.Organization.TryGetValue(file, out var organization) && GitHubReleaseLibraryFile.Repository.TryGetValue(file, out var repository))
                return GitHubRepositoryRef.FromName(organization, repository);
        }

        return null;
    }
}
