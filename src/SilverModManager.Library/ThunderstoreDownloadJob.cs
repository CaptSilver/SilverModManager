using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.HttpDownloader;
using SilverModManager.Networking.Thunderstore;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Library;

/// <summary>
/// Downloads the zip of one Thunderstore package version, checks it against a SHA-256 when one is known and adds
/// it to the library together with where it came from. A file whose bytes do not match is never added.
/// </summary>
/// <remarks>
/// Thunderstore publishes no digest for a zip, so without an expected digest the computed one becomes the pin
/// (trust on first use).
/// </remarks>
[PublicAPI]
public class ThunderstoreDownloadJob : IJobDefinitionWithStart<ThunderstoreDownloadJob, ThunderstoreLibraryFile.ReadOnly>
{
    public required ThunderstoreVersionRef Version { get; init; }

    /// <summary>
    /// The slug of the community the package is installed for, e.g. <c>repo</c>.
    /// </summary>
    public required string Community { get; init; }

    /// <summary>
    /// The digest the downloaded bytes must have, if one is already pinned.
    /// </summary>
    public Sha256Value? ExpectedSha256 { get; init; }

    private IThunderstoreApi Api { get; }
    private HttpClient Client { get; }
    private IServiceProvider ServiceProvider { get; }

    private ThunderstoreDownloadJob(IServiceProvider serviceProvider, IThunderstoreApi api, HttpClient client)
    {
        ServiceProvider = serviceProvider;
        Api = api;
        Client = client;
    }

    public static IJobTask<ThunderstoreDownloadJob, ThunderstoreLibraryFile.ReadOnly> Create(
        IServiceProvider provider,
        ThunderstoreVersionRef version,
        string community,
        Sha256Value? expected = null)
    {
        return Create(provider, provider.GetRequiredService<IThunderstoreApi>(), provider.GetRequiredService<HttpClient>(), version, community, expected);
    }

    public static IJobTask<ThunderstoreDownloadJob, ThunderstoreLibraryFile.ReadOnly> Create(
        IServiceProvider provider,
        IThunderstoreApi api,
        HttpClient client,
        ThunderstoreVersionRef version,
        string community,
        Sha256Value? expected)
    {
        if (string.IsNullOrWhiteSpace(community))
            throw new ArgumentException("A community slug is required.", nameof(community));

        var job = new ThunderstoreDownloadJob(provider, api, client)
        {
            Version = version,
            Community = community,
            ExpectedSha256 = expected,
        };
        return provider.GetRequiredService<IJobMonitor>().Begin<ThunderstoreDownloadJob, ThunderstoreLibraryFile.ReadOnly>(job);
    }

    public async ValueTask<ThunderstoreLibraryFile.ReadOnly> StartAsync(IJobContext<ThunderstoreDownloadJob> context)
    {
        var record = await Api.FetchVersion(Version, context.CancellationToken)
            ?? throw new ThunderstoreIntegrityException($"Thunderstore has no version {Version}.");

        if (!record.IsActive)
            throw new ThunderstoreIntegrityException($"Version {Version} has been withdrawn from Thunderstore.");

        var temporaryFileManager = ServiceProvider.GetRequiredService<TemporaryFileManager>();
        await using var folder = temporaryFileManager.CreateFolder();

        // The file keeps Thunderstore's own name: it becomes the library item's name, and a lock entry without a
        // stored hash finds the file again by it.
        var downloaded = folder.Path.Combine(Version.ArchiveFileName);

        try
        {
            await context.YieldAsync();
            var download = new HttpDownloadJob
            {
                Uri = Api.DownloadUri(Version),
                DownloadPageUri = new Uri($"https://thunderstore.io/c/{Uri.EscapeDataString(Community)}/p/{Version.Package.Namespace}/{Version.Package.Name}/"),
                Destination = downloaded,
                Client = Client,
                Logger = ServiceProvider.GetRequiredService<ILogger<HttpDownloadJob>>(),
            };
            await ServiceProvider.GetRequiredService<IJobMonitor>().Begin<HttpDownloadJob, AbsolutePath>(download);

            Sha256Value actual;
            await using (var stream = downloaded.Read())
            {
                actual = await Sha256Hasher.HashAsync(stream, cancellationToken: context.CancellationToken);
            }

            if (ExpectedSha256 is { } expected && !expected.Equals(actual))
            {
                throw new ThunderstoreIntegrityException(
                    $"SHA-256 mismatch for {Version}: expected {expected} (the pinned digest), got {actual}."
                );
            }

            await context.YieldAsync();
            using var tx = ServiceProvider.GetRequiredService<IConnection>().BeginTransaction();
            var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, downloaded);

            var model = new ThunderstoreLibraryFile.New(tx, libraryFile.Id)
            {
                LibraryFile = libraryFile,
                Namespace = Version.Package.Namespace,
                Name = Version.Package.Name,
                VersionNumber = Version.Version.ToString(),
                Community = Community,
                PinnedSha256 = actual,
            };

            if (record.Uuid4 is { } versionUuid) tx.Add(model.Id, ThunderstoreLibraryFile.VersionUuid, versionUuid);

            var result = await tx.Commit();
            return result.Remap(model);
        }
        finally
        {
            // The folder is removed on dispose; this also covers a file that outlives a failed dispose.
            if (downloaded.FileExists) downloaded.Delete();
        }
    }
}

/// <summary>
/// A Thunderstore version could not be found, was withdrawn, or did not match the digest it was expected to have.
/// </summary>
[PublicAPI]
public class ThunderstoreIntegrityException(string message) : Exception(message);
