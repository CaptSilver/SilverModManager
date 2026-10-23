using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Backend.Games.Locators;

/// <summary>
/// Asks a game which file it launches without an install to ask about.
/// </summary>
internal static class PrimaryFileProbe
{
    /// <summary>
    /// The path of the file a game launches, relative to its root, or null when it launches something outside the
    /// game folder or cannot say. <see cref="IGameData.GetPrimaryFile"/> needs an installation, so it is asked
    /// through a probe one; games only look at the target OS and store there, so a Windows install answers it.
    /// </summary>
    public static RelativePath? Find(IGameData game, IFileSystem fileSystem, IGameLocator locator, ILogger logger)
    {
        try
        {
            var probePath = fileSystem.GetKnownPath(KnownPath.HomeDirectory);
            var probe = new GameLocatorResult
            {
                Game = game,
                Path = probePath,
                Store = GameStore.Lutris,
                StoreIdentifier = "",
                LocatorIds = [],
                Locator = locator,
                Platform = OSPlatform.Windows,
            };
            var installation = new GameInstallation(probe, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, probePath)));
            var primaryFile = game.GetPrimaryFile(installation);
            if (primaryFile.LocationId == LocationId.Game) return primaryFile.Path;

            logger.LogWarning("{Game} launches a file outside its game folder; it will not be matched to a folder by that file", game.DisplayName);
            return null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Cannot tell which executable {Game} launches; it will not be matched to a folder by that file", game.DisplayName);
            return null;
        }
    }
}
