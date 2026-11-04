using JetBrains.Annotations;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Thrown before anything is written when the loadout has to put a file on disk but the app holds no copy of its
/// bytes: a file adopted from the game folder without a backup that is no longer there, or an archive that was removed.
/// </summary>
[PublicAPI]
public class MissingFileBytesException(IReadOnlyList<GamePath> paths) : UserFacingException(MessageFor(paths))
{
    /// <summary>The files that have no stored bytes.</summary>
    public IReadOnlyList<GamePath> Paths { get; } = paths;

    /// <inheritdoc />
    public override string Title => "The loadout was not applied";

    private static string MessageFor(IReadOnlyList<GamePath> paths)
    {
        const int listed = 5;
        var names = string.Join(", ", paths.Take(listed).Select(p => MarkdownText.Code(p.Path.ToString())));
        var more = paths.Count > listed ? $" and {paths.Count - listed} more" : "";
        return $"The app has no copy of {names}{more}, so it cannot put {(paths.Count == 1 ? "it" : "them")} back in the game folder. " +
               "Restore the file yourself or remove it from the loadout, then apply again.";
    }
}
