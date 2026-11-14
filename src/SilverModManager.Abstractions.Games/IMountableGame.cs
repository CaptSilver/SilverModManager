using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// A game with folders a FUSE mount can serve in place of deploying files into them. A game that does not implement
/// this has no folder the fuse deployment backend accepts.
/// </summary>
/// <remarks>
/// Callers hold an <see cref="SilverModManager.Sdk.Games.IGameData"/>; the registered game instance behind it is
/// what implements this, as with <see cref="IDxgiGame"/>.
/// </remarks>
public interface IMountableGame
{
    /// <summary>
    /// The folders that can be mounted, each a whole folder the game reads mods from and writes nothing generated into.
    /// </summary>
    IReadOnlyList<GamePath> MountRoots(GameInstallation installation);
}
