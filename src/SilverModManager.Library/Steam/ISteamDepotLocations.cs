using NexusMods.Paths;
using SilverModManager.Abstractions.Steam.Values;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Library.Steam;

/// <summary>Where Steam keeps its saved manifests for an app: the client root, and the install's own folder when one is managed.</summary>
public readonly record struct SteamDepotLocation(AbsolutePath SteamRoot, AbsolutePath? InstallPath);

/// <summary>
/// The Steam client roots a manifest of an app may be cached under. Fetching a manifest needs no root, because it goes
/// through the login; only reading a cached one without logging in does.
/// </summary>
public interface ISteamDepotLocations
{
    /// <summary>The roots to look in for <paramref name="app"/>, the ones of its managed installs first.</summary>
    IReadOnlyList<SteamDepotLocation> For(AppId app);
}

/// <summary>
/// Takes the roots from the Steam installs the app has located. A machine with no managed Steam game has none, which
/// only means every manifest is fetched through the login.
/// </summary>
internal sealed class InstalledSteamDepotLocations(IGameRegistry registry) : ISteamDepotLocations
{
    public IReadOnlyList<SteamDepotLocation> For(AppId app)
    {
        var found = new List<SteamDepotLocation>();
        var others = new List<SteamDepotLocation>();
        foreach (var installation in registry.LocateGameInstallations())
        {
            var locator = installation.LocatorResult;
            if (locator.Store != GameStore.Steam || locator.StoreRoot is not { } root) continue;
            var location = new SteamDepotLocation(root, locator.Path);
            (locator.StoreIdentifier == app.Value.ToString() ? found : others).Add(location);
        }

        found.AddRange(others);
        return found;
    }
}
