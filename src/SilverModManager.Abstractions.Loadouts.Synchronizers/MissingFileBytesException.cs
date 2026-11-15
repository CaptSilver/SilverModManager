using JetBrains.Annotations;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Errors;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Thrown before anything is written when the loadout or a reset has to put a file on disk but the app holds no copy
/// of its bytes: a file adopted from the game folder without a backup that is no longer there, or an archive that was
/// removed.
/// </summary>
[PublicAPI]
public class MissingFileBytesException : UserFacingException
{
    private readonly bool _forReset;

    /// <summary>The refusal of a loadout apply.</summary>
    public MissingFileBytesException(IReadOnlyList<GamePath> paths) : this(paths, forReset: false) { }

    private MissingFileBytesException(IReadOnlyList<GamePath> paths, bool forReset) : base(MessageFor(paths, forReset))
    {
        Paths = paths;
        _forReset = forReset;
    }

    /// <summary>
    /// The refusal of a reset to the game's original files. There is no loadout to take the file out of, so the
    /// advice is to get the file back instead.
    /// </summary>
    public static MissingFileBytesException ForReset(IReadOnlyList<GamePath> paths) => new(paths, forReset: true);

    /// <summary>The files that have no stored bytes.</summary>
    public IReadOnlyList<GamePath> Paths { get; }

    /// <inheritdoc />
    public override string Title => _forReset ? "The game folder was not reset" : "The loadout was not applied";

    private static string MessageFor(IReadOnlyList<GamePath> paths, bool forReset)
    {
        const int listed = 5;
        var names = string.Join(", ", paths.Take(listed).Select(p => MarkdownText.Code(p.Path.ToString())));
        var more = paths.Count > listed ? $" and {paths.Count - listed} more" : "";
        var advice = forReset
            ? "Let the store verify the game's files, or put the file back yourself, then try again."
            : "Restore the file yourself or remove it from the loadout, then apply again.";
        return $"The app has no copy of {names}{more}, so it cannot put {(paths.Count == 1 ? "it" : "them")} back in the game folder. " + advice;
    }
}
