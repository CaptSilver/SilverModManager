using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Library;
using NexusMods.Library.Lock;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Networking.Thunderstore;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Hashes;
using NexusMods.Sdk.Library;
using NexusMods.Sdk.Thunderstore;

namespace NexusMods.Library.Sources;

/// <summary>
/// Thunderstore package zips. The lock carries no namespace/name split, only the archive name
/// <c>Namespace-Name-Version.zip</c>, so a download looks the package up under every possible split. Services are
/// resolved when first used so a host without Thunderstore support can still list, export and verify a lock.
/// </summary>
internal sealed class ThunderstoreLibrarySource(IServiceProvider provider) : ILibrarySource
{
    public string Name => LockSources.Thunderstore;

    public ValueTask<LibrarySourceEntry?> Describe(string groupName, LibraryItem.ReadOnly item, LibraryFile.ReadOnly file, CancellationToken cancellationToken)
    {
        if (!file.TryGetAsThunderstoreLibraryFile(out var package)) return ValueTask.FromResult<LibrarySourceEntry?>(null);

        var asset = file.FileName.ToString();
        if (!ThunderstoreLibraryFile.PinnedSha256.TryGetValue(package, out var pinned))
            throw new LockException($"'{asset}' is a Thunderstore package without a pinned SHA-256, so it cannot be pinned.");

        var entry = new LibrarySourceEntry(LockExport.SanitizeId($"{package.Namespace}-{package.Name}", asset), package.VersionNumber, null, asset, pinned.ToString(), Name);
        return ValueTask.FromResult<LibrarySourceEntry?>(entry);
    }

    public string? WhyNotDownloadable(LibrarySourceEntry entry, LibrarySourceContext context)
    {
        if (!ThunderstoreDependencyString.TryParseArchiveFileName(entry.Asset, out _))
            return $"{entry.Id}: '{entry.Asset}' is not a Thunderstore package archive name (Namespace-Name-Version.zip).";

        return context.Game is { } game && ThunderstoreGame.For(game) is not null
            ? null
            : $"{entry.Id}: {(context.Game is { } other ? other.DisplayName : "the loadout's game")} has no Thunderstore support, so {entry.Asset} cannot be downloaded.";
    }

    public async ValueTask<LibraryFile.ReadOnly> Download(LibrarySourceEntry entry, Sha256Value expected, LibrarySourceContext context, CancellationToken cancellationToken)
    {
        if (!ThunderstoreDependencyString.TryParseArchiveFileName(entry.Asset, out var dependency))
            throw new InvalidOperationException($"{entry.Id}: '{entry.Asset}' is not a Thunderstore package archive name.");
        if (context.Game is not { } gameData || ThunderstoreGame.For(gameData) is not { } game)
            throw new InvalidOperationException($"{entry.Id}: {entry.Asset} cannot be downloaded without a game that has Thunderstore support.");

        var package = await FindPackage(entry, dependency, cancellationToken);
        var version = new ThunderstoreVersionRef(package, dependency.VersionNumber);

        // The download job reuses a version the library holds without looking at its bytes, and the importer removes
        // whatever file fails the lock's digest. Refuse here so that never takes a different copy with it.
        if (FindOtherCopy(context.Connection, version, expected) is not null)
            throw new InvalidOperationException($"{entry.Id}: the library already holds a different {entry.Asset} than the one the lock pins; remove it from the library first.");

        var request = new ThunderstoreInstallRequest(version, game, WithDependencies: false, DependencyPolicy.Latest, expected, new Dictionary<ThunderstorePackageRef, ThunderstoreVersionNumber>());
        var files = await provider.GetRequiredService<IThunderstoreSource>().AddAsync(request, cancellationToken);
        return files[^1];
    }

    private async ValueTask<ThunderstorePackageRef> FindPackage(LibrarySourceEntry entry, ThunderstoreDependencyString dependency, CancellationToken cancellationToken)
    {
        var api = provider.GetRequiredService<IThunderstoreApi>();
        var found = new List<ThunderstorePackageRef>();
        foreach (var candidate in dependency.Candidates())
        {
            if (await api.FetchPackage(candidate, cancellationToken) is not null) found.Add(candidate);
        }

        return found.Count switch
        {
            1 => found[0],
            0 => throw new InvalidOperationException($"{entry.Id}: Thunderstore has no package that {entry.Asset} could belong to."),
            _ => throw new InvalidOperationException($"{entry.Id}: {entry.Asset} could belong to more than one Thunderstore package ({string.Join(", ", found)})."),
        };
    }

    private static ThunderstoreLibraryFile.ReadOnly? FindOtherCopy(IConnection connection, ThunderstoreVersionRef version, Sha256Value expected)
    {
        var number = version.Version.ToString();
        foreach (var file in ThunderstoreLibraryFile.All(connection.Db))
        {
            if (file.Namespace != version.Package.Namespace || file.Name != version.Package.Name || file.VersionNumber != number) continue;
            if (!file.PinnedSha256.Equals(expected)) return file;
        }

        return null;
    }
}
