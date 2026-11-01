using SilverModManager.Abstractions.Library;
using SilverModManager.Networking.Thunderstore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Thunderstore;

// Not SilverModManager.Library.ModSources: App.UI has a static class ModSources and the names would clash.
namespace SilverModManager.Library;

/// <summary>
/// Items downloaded as a package from Thunderstore.
/// </summary>
internal sealed class ThunderstoreModSource(IThunderstoreCatalogSource catalogs, ThunderstoreUpdateChecker checker) : IModSource
{
    public string DisplayName => "Thunderstore";

    public bool Owns(LibraryItem.ReadOnly item) => TryGetFile(item, out _);

    public Uri? PageUri(LibraryItem.ReadOnly item)
        => TryGetPackage(item, out var community, out var package) ? ThunderstoreUrls.PackagePage(community, package) : null;

    public Uri? ChangelogUri(LibraryItem.ReadOnly item)
        => TryGetPackage(item, out var community, out var package) ? ThunderstoreUrls.Changelog(community, package) : null;

    public async Task CheckUpdates(IGameData game, bool force, CancellationToken cancellationToken)
    {
        if (ThunderstoreGame.For(game) is not { } thunderstoreGame) return;

        var community = thunderstoreGame.ThunderstoreCommunity;
        if (force || catalogs.IsStale(community)) await catalogs.Refresh(community, cancellationToken);

        // The catalog is loaded even when it was not refreshed: the checker starts empty each run, and the
        // cached listing is what it compares the library against.
        checker.Refresh(await catalogs.Load(community, cancellationToken));
    }

    private static bool TryGetFile(LibraryItem.ReadOnly item, out ThunderstoreLibraryFile.ReadOnly file)
    {
        file = default;
        return item.TryGetAsLibraryFile(out var libraryFile) && libraryFile.TryGetAsThunderstoreLibraryFile(out file);
    }

    // A row whose team or package name Thunderstore would not accept is owned but has no address to build.
    private static bool TryGetPackage(LibraryItem.ReadOnly item, out string community, out ThunderstorePackageRef package)
    {
        community = "";
        package = default;
        if (TryGetFile(item, out var file) is false) return false;

        community = file.Community;
        return ThunderstorePackageRef.TryCreate(file.Namespace, file.Name, out package);
    }
}
