using SilverModManager.Abstractions.Games;

namespace SilverModManager.Games.Larian.Engine;

/// <summary>A game built on <see cref="ALarianGame{TSelf}"/>, seen without its type parameter.</summary>
public interface ILarianGame : IGame
{
    LarianGameProfile Profile { get; }

    /// <summary>Whether the game folder is backed up whole, which the player switches on in the settings.</summary>
    bool DoFullGameBackup { get; }
}
