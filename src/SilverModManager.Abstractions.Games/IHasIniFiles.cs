using SilverModManager.Sdk.Ini;

namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Implemented by a game that reads INI files, so a loadout can own them. A game without it gets no "Own the game
/// INIs" choice.
/// </summary>
public interface IHasIniFiles
{
    /// <summary>
    /// The INI files the game reads, in the order it resolves them.
    /// </summary>
    IniSet IniFiles { get; }
}
