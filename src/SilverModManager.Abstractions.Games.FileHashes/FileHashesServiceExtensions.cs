using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// Lookups that need both an installation and the locator ids a loadout recorded.
/// </summary>
public static class FileHashesServiceExtensions
{
    /// <summary>
    /// The game files of the build <paramref name="locatorIds"/> name. When they are the ids the installation itself
    /// reports, the files its baseline recorded from the manifest are included, since only the installation's
    /// baseline knows them. Ids of another build (a loadout recorded before a game update) get the depot manifests'
    /// rows alone, because the baseline describes the files that are installed now.
    /// </summary>
    public static IEnumerable<GameFileRecord> GetGameFiles(this IFileHashesService service, GameInstallation installation, IEnumerable<LocatorId> locatorIds)
    {
        var ids = locatorIds.Distinct().ToArray();
        var installed = installation.LocatorResult.LocatorIds.Distinct().ToHashSet();
        return installed.SetEquals(ids)
            ? service.GetGameFiles(installation)
            : service.GetGameFiles((installation.LocatorResult.Store, ids));
    }
}
