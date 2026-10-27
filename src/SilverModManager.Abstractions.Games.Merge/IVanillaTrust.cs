using NexusMods.Hashing.xxHash3;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// Decides whether the installed copy of a game file can stand in for the vanilla ancestor of a merge. A file the
/// player or another tool has changed would make every difference between it and a mod read as a mod edit.
/// </summary>
public interface IVanillaTrust
{
    /// <summary>
    /// Whether the installed copy at <paramref name="path"/> is the file a baseline recorded. Trust needs positive
    /// evidence: the baseline lists the path and <paramref name="currentHash"/>, the hash of the bytes the caller just
    /// read, equals the recorded one. A missing baseline, an unlisted path, a file listed non-vanilla or a file changed
    /// since the baseline was built is untrusted; <paramref name="reason"/> is a phrase for the note.
    /// </summary>
    bool IsTrusted(GameInstallation installation, GamePath path, Hash currentHash, out string reason);
}
