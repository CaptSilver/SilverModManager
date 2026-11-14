using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.GitHub;
using SilverModManager.Networking.GitHub.DTOs;
using SilverModManager.Networking.HttpDownloader;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Library;

/// <summary>
/// Downloads one asset of a GitHub release, checks it against a SHA-256 and adds it to the library together
/// with where it came from. A file whose bytes do not match is never added.
/// </summary>
/// <remarks>
/// With a pinned digest the bytes must match it. Without one, the computed digest becomes the pin (trust on
/// first use). Either way, a digest that GitHub reports for the asset must match the bytes too.
/// </remarks>
[PublicAPI]
public class AddGitHubReleaseAssetJob : IJobDefinitionWithStart<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly>
{
    public required GitHubRepositoryRef Repository { get; init; }

    /// <summary>
    /// The release tag, or null for the release GitHub marks as latest.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// The asset name must end with this.
    /// </summary>
    public required string Suffix { get; init; }

    /// <summary>
    /// Preferred part of the asset name when several assets end with the suffix.
    /// </summary>
    public string? NameHint { get; init; }

    /// <summary>
    /// Where the downloaded file goes: the games it is recorded for.
    /// </summary>
    public required IAcquisitionContext Acquisition { get; init; }

    /// <summary>
    /// The digest the downloaded bytes must have, if one is already pinned.
    /// </summary>
    public Sha256Value? ExpectedSha256 { get; init; }

    /// <summary>
    /// Fail when GitHub reports no digest for the asset and none is pinned, instead of trusting the first download.
    /// </summary>
    public bool RequireAdvertisedDigest { get; init; }

    /// <summary>
    /// The digests a checksum file of the release lists, by asset name. The downloaded bytes must match the one listed
    /// for the asset. The file comes from the same uploader as the asset, so it catches a damaged transfer, not a
    /// compromised release: it does not turn a first-use pin into one GitHub vouched for.
    /// </summary>
    public IReadOnlyDictionary<string, Sha256Value>? Sidecar { get; init; }

    /// <summary>
    /// The numeric id of a repository that is addressed by name, recorded beside the name so later checks can follow
    /// the repository through a rename.
    /// </summary>
    public ulong? KnownRepositoryId { get; init; }

    private IGitHubApi Api { get; }
    private HttpClient Client { get; }
    private IServiceProvider ServiceProvider { get; }

    private AddGitHubReleaseAssetJob(IServiceProvider serviceProvider, IGitHubApi api, HttpClient client)
    {
        ServiceProvider = serviceProvider;
        Api = api;
        Client = client;
    }

    public static IJobTask<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly> Create(
        IServiceProvider provider,
        GitHubRepositoryRef repository,
        string? tag,
        string suffix,
        IAcquisitionContext acquisition,
        string? nameHint = null,
        Sha256Value? expectedSha256 = null,
        bool requireAdvertisedDigest = false,
        IReadOnlyDictionary<string, Sha256Value>? sidecar = null,
        ulong? knownRepositoryId = null)
    {
        return Create(provider, provider.GetRequiredService<IGitHubApi>(), provider.GetRequiredService<HttpClient>(), repository, tag, suffix, acquisition, nameHint, expectedSha256, requireAdvertisedDigest, sidecar, knownRepositoryId);
    }

    internal static IJobTask<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly> Create(
        IServiceProvider provider,
        IGitHubApi api,
        HttpClient client,
        GitHubRepositoryRef repository,
        string? tag,
        string suffix,
        IAcquisitionContext acquisition,
        string? nameHint,
        Sha256Value? expectedSha256,
        bool requireAdvertisedDigest = false,
        IReadOnlyDictionary<string, Sha256Value>? sidecar = null,
        ulong? knownRepositoryId = null)
    {
        if (repository.Id is null && repository.Organization is null)
            throw new ArgumentException("The repository is neither an id nor a name.", nameof(repository));

        var job = new AddGitHubReleaseAssetJob(provider, api, client)
        {
            Repository = repository,
            Tag = tag,
            Suffix = suffix,
            Acquisition = acquisition,
            NameHint = nameHint,
            ExpectedSha256 = expectedSha256,
            RequireAdvertisedDigest = requireAdvertisedDigest,
            Sidecar = sidecar,
            KnownRepositoryId = knownRepositoryId,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<AddGitHubReleaseAssetJob, GitHubReleaseLibraryFile.ReadOnly>(job);
    }

    public async ValueTask<GitHubReleaseLibraryFile.ReadOnly> StartAsync(IJobContext<AddGitHubReleaseAssetJob> context)
    {
        var release = await FetchRelease(context.CancellationToken);
        var asset = ReleaseAssets.Pick(release, Suffix, NameHint)
            ?? throw new ReleaseAssetIntegrityException($"Release {MarkdownText.Code(release.TagName)} of {MarkdownText.Code(Repository)} has no asset ending in {MarkdownText.Code(Suffix)}.");

        // The name becomes a file name in the library, so it must not be able to name a path.
        if (asset.Name != Path.GetFileName(asset.Name))
            throw new ReleaseAssetIntegrityException($"Asset name {MarkdownText.Code(asset.Name)} of release {MarkdownText.Code(release.TagName)} of {MarkdownText.Code(Repository)} is not a plain file name.");

        var advertised = asset.GetSha256();
        if (RequireAdvertisedDigest && advertised is null && ExpectedSha256 is null)
            throw new ReleaseAssetIntegrityException($"Asset {MarkdownText.Code(asset.Name)} of release {MarkdownText.Code(release.TagName)} of {MarkdownText.Code(Repository)} has no SHA-256 digest, so the download cannot be verified.");

        var temporaryFileManager = ServiceProvider.GetRequiredService<TemporaryFileManager>();
        await using var folder = temporaryFileManager.CreateFolder();
        var downloaded = folder.Path.Combine(asset.Name);

        try
        {
            await context.YieldAsync();
            var download = new HttpDownloadJob
            {
                Uri = new Uri(asset.BrowserDownloadUrl),
                DownloadPageUri = new Uri(release.HtmlUrl),
                Destination = downloaded,
                Client = Client,
                Logger = ServiceProvider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            };
            var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();
            var downloading = monitor.Begin<HttpDownloadJob, AbsolutePath>(download);
            // The download is a job of its own, so cancelling this one doesn't reach it unless passed on.
            await using (context.CancellationToken.Register(() => monitor.Cancel(downloading)))
            {
                await downloading;
            }

            var actual = await FileHashing.Sha256(downloaded, context.CancellationToken);

            Verify(asset, release.TagName, ExpectedSha256, actual, "the pinned digest");
            Verify(asset, release.TagName, advertised, actual, "the digest GitHub reports for the asset");
            if (Sidecar is not null && Sidecar.TryGetValue(asset.Name, out var listed))
                Verify(asset, release.TagName, listed, actual, "the checksum file of the release");

            await context.YieldAsync();
            using var tx = ServiceProvider.GetRequiredService<IConnection>().BeginTransaction();
            var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, downloaded, Acquisition);

            var model = new GitHubReleaseLibraryFile.New(tx, libraryFile.Id)
            {
                LibraryFile = libraryFile,
                Tag = release.TagName,
                AssetName = asset.Name,
                PinnedSha256 = actual,
            };

            // GitHub vouched for the bytes when it reported a digest, and a pin that existed before the download was
            // checked against something. Otherwise the pin is only what the first download happened to contain.
            tx.Add(model.Id, GitHubReleaseLibraryFile.DigestAdvertised, advertised is not null || ExpectedSha256 is not null);

            if (Repository.Id is { } id) tx.Add(model.Id, GitHubReleaseLibraryFile.RepositoryId, id);
            if (Repository.Organization is { } organization) tx.Add(model.Id, GitHubReleaseLibraryFile.Organization, organization);
            if (Repository.Repository is { } repository) tx.Add(model.Id, GitHubReleaseLibraryFile.Repository, repository);
            if (Repository.Id is null && KnownRepositoryId is { } known) tx.Add(model.Id, GitHubReleaseLibraryFile.RepositoryId, known);

            var result = await tx.Commit();
            return result.Remap(model);
        }
        finally
        {
            // The folder is removed on dispose; this also covers a file that outlives a failed dispose.
            if (downloaded.FileExists) downloaded.Delete();
        }
    }

    private async ValueTask<Release> FetchRelease(CancellationToken cancellationToken)
    {
        Release? release;
        // A name can pass to another owner after a rename or a delete, so a repository whose id is already known is
        // fetched by that id: the download comes from the repository the update check looked at.
        if ((Repository.Id ?? KnownRepositoryId) is { } id)
        {
            release = Tag is null
                ? await Api.FetchLatestReleaseById(checked((long)id), cancellationToken)
                : await Api.FetchReleaseByTagById(checked((long)id), Tag, cancellationToken);
        }
        else if (Repository is { Organization: { } organization, Repository: { } repository })
        {
            release = Tag is null
                ? await Api.FetchMarkedLatestRelease(organization, repository, cancellationToken)
                : await Api.FetchReleaseByTag(organization, repository, Tag, cancellationToken);
        }
        else
        {
            throw new InvalidOperationException($"The repository is neither an id nor a name: {Repository}.");
        }

        return release ?? throw new ReleaseAssetIntegrityException($"No release {MarkdownText.Code(Tag ?? "(latest)")} found for {MarkdownText.Code(Repository)}.");
    }

    private void Verify(Asset asset, string tag, Sha256Value? expected, Sha256Value actual, string source)
    {
        if (expected is not { } value) return;
        if (value.Equals(actual)) return;

        throw new ReleaseAssetIntegrityException(
            $"SHA-256 mismatch for asset '{asset.Name}' of release '{tag}' of {Repository}: expected {value} ({source}), got {actual}."
        );
    }
}

/// <summary>
/// A release asset could not be found or did not match the digest it was expected to have.
/// </summary>
[PublicAPI]
public class ReleaseAssetIntegrityException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "The release asset was rejected";
}
