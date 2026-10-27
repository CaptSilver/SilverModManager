using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// Decides whether the installed copy of a game file can stand in for the vanilla ancestor of a merge. A file the
/// player or another tool has changed would make every difference between it and a mod read as a mod edit.
/// </summary>
public interface IVanillaTrust
{
    /// <summary>
    /// Whether the installed copy at <paramref name="path"/> matches the store's file list. Untrusted when there is no
    /// baseline or the file is listed non-vanilla; <paramref name="reason"/> is a phrase for the note.
    /// </summary>
    bool IsTrusted(GameInstallation installation, GamePath path, out string reason);
}
