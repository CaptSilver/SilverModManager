using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>Where a <see cref="ModLocation"/> starts: the game folder or one of the Windows known folders.</summary>
public enum ModLocationRoot
{
    GameFolder,
    Documents,
    LocalAppData,
    RoamingAppData,
}

/// <summary>
/// The folder a game reads its mods from. Outside the game folder, <see cref="Folder"/> is part of the location the
/// definition maps (see <see cref="SimpleLocations"/>), so the shared location ids carry no game-specific name.
/// </summary>
public sealed record ModLocation(ModLocationRoot Root, RelativePath Folder)
{
    /// <summary>
    /// The game path of <paramref name="subPath"/> inside this folder. In the game folder that is below
    /// <see cref="Folder"/> in <see cref="LocationId.Game"/>; elsewhere the location already ends at
    /// <see cref="Folder"/>, so the path starts at it.
    /// </summary>
    public GamePath ToGamePath(RelativePath subPath) => Root == ModLocationRoot.GameFolder
        ? new GamePath(LocationId.Game, Folder.Join(subPath))
        : new GamePath(SimpleLocations.For(Root), subPath);
}
