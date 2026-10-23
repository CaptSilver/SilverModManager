namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// How a recognizer knows a piece of content belongs to the game.
/// </summary>
public enum GameInstalledConfidence
{
    /// <summary>The game's own record of installed content names it.</summary>
    GameRecord,

    /// <summary>No record names it; the file name looks like the game's content.</summary>
    FileNamePattern,
}
