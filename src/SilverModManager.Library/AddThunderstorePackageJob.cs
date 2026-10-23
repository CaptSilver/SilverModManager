using JetBrains.Annotations;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Library;

/// <summary>
/// Adds a Thunderstore package version to the library, together with the dependencies it needs. Which versions
/// those are is the resolver's call; a version the library already holds is reused, any other is downloaded by
/// <see cref="ThunderstoreDownloadJob"/>.
/// </summary>
/// <remarks>
/// Each file is committed by its own download, so when a later one fails the earlier ones stay in the library.
/// They were verified, and a retry finds them instead of downloading them again.
/// </remarks>
[PublicAPI]
public class AddThunderstorePackageJob : IJobDefinitionWithStart<AddThunderstorePackageJob, IReadOnlyList<LibraryFile.ReadOnly>>
{
    public required ThunderstoreInstallRequest Request { get; init; }

    private IServiceProvider ServiceProvider { get; }

    private AddThunderstorePackageJob(IServiceProvider serviceProvider) => ServiceProvider = serviceProvider;

    public static IJobTask<AddThunderstorePackageJob, IReadOnlyList<LibraryFile.ReadOnly>> Create(IServiceProvider provider, ThunderstoreInstallRequest request)
    {
        var job = new AddThunderstorePackageJob(provider) { Request = request };
        return provider.GetRequiredService<IJobMonitor>().Begin<AddThunderstorePackageJob, IReadOnlyList<LibraryFile.ReadOnly>>(job);
    }

    public async ValueTask<IReadOnlyList<LibraryFile.ReadOnly>> StartAsync(IJobContext<AddThunderstorePackageJob> context)
    {
        var token = context.CancellationToken;
        var versions = await ResolveVersions(token);

        var monitor = ServiceProvider.GetRequiredService<IJobMonitor>();
        var connection = ServiceProvider.GetRequiredService<IConnection>();
        var community = Request.Game.ThunderstoreCommunity;
        var files = new List<LibraryFile.ReadOnly>(versions.Count);

        foreach (var version in versions)
        {
            token.ThrowIfCancellationRequested();

            // The library is the only dedupe: nothing else says a version was downloaded already.
            if (FindInLibrary(connection.Db, version) is { } existing)
            {
                files.Add(existing.AsLibraryFile());
                continue;
            }

            // Only the root has a pinned digest to check; a dependency is pinned by this first download.
            var expected = version == Request.Root ? Request.ExpectedSha256 : null;
            var download = ThunderstoreDownloadJob.Create(ServiceProvider, version, community, expected);
            await using var registration = token.Register(() => monitor.Cancel(download));
            var added = await download;
            files.Add(added.AsLibraryFile());
        }

        return files;
    }

    private async ValueTask<IReadOnlyList<ThunderstoreVersionRef>> ResolveVersions(CancellationToken cancellationToken)
    {
        if (!Request.WithDependencies) return [Request.Root];

        var game = Request.Game;
        var resolve = new ThunderstoreDependencyResolver.ResolveRequest(
            Request.Root,
            Request.Policy,
            ParsePackage(game.ThunderstoreLoaderPackage),
            game.ThunderstoreLoaderAliases.Select(ParsePackage).ToArray(),
            Request.InstalledInLoadout
        );
        return await ServiceProvider.GetRequiredService<ThunderstoreDependencyResolver>().Resolve(resolve, cancellationToken);
    }

    private static ThunderstoreLibraryFile.ReadOnly? FindInLibrary(IDb db, ThunderstoreVersionRef version)
    {
        var versionNumber = version.Version.ToString();
        foreach (var file in ThunderstoreLibraryFile.All(db))
        {
            if (file.Namespace == version.Package.Namespace && file.Name == version.Package.Name && file.VersionNumber == versionNumber)
                return file;
        }

        return null;
    }

    // Names carry no hyphen but the namespace may, so the last dash is the split.
    private static ThunderstorePackageRef ParsePackage(string text)
    {
        var dash = text.LastIndexOf('-');
        if (dash > 0 && ThunderstorePackageRef.TryCreate(text[..dash], text[(dash + 1)..], out var package)) return package;
        throw new ArgumentException($"'{text}' is not a Thunderstore package name of the form Namespace-Name.", nameof(text));
    }
}
