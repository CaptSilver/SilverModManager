using NexusMods.Paths;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Games;

/// <summary>
/// A folder under a well-known directory, such as <c>Saved Games/Studio/Game</c> under the home directory.
/// </summary>
public readonly record struct UserDataFolder(KnownPath Root, string RelativePath);

/// <summary>
/// Implemented by a game that keeps save or settings folders the app deliberately does not manage, but that
/// Health Check should still look after.
/// </summary>
/// <remarks>
/// The folders are resolved through the installation's own file system, so a game running under Proton is
/// checked inside its prefix. The game's mapped locations are checked as well and need no declaration.
/// </remarks>
public interface IGameUserDataFolders
{
    /// <summary>
    /// The folders worth checking, besides the game's mapped locations.
    /// </summary>
    IReadOnlyList<UserDataFolder> UserDataFolders { get; }
}

public static class GameUserDataFolders
{
    /// <summary>
    /// The folders <paramref name="game"/> declares, or none.
    /// </summary>
    public static IReadOnlyList<UserDataFolder> For(IGameData game)
    {
        return game is IGameUserDataFolders declared ? declared.UserDataFolders : [];
    }
}
