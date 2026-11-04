using NexusMods.Paths;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Why an Apply cannot write a file the loadout names. Either way the store holds no copy of the bytes.
/// </summary>
public enum UnwritableReason
{
    /// <summary>The game folder does not have the file.</summary>
    NotOnDisk,

    /// <summary>The game folder has a file at the path, with other contents than the loadout wants.</summary>
    DifferentOnDisk,
}

/// <summary>A file an Apply cannot write, and why.</summary>
public readonly record struct UnwritableFile(GamePath Path, UnwritableReason Reason)
{
    /// <summary>One line for a person: what is wrong with the file.</summary>
    public string Describe() => Reason == UnwritableReason.NotOnDisk
        ? $"{Name(Path)}: no copy kept, and it is not in the game folder"
        : $"{Name(Path)}: no copy kept of the version the loadout wants, and the game folder has another";

    /// <summary>
    /// How a path is written for a person and accepted back by <see cref="ParsePath"/>: a path in the game folder
    /// as it is, one in another location as <c>{Location}/path</c>.
    /// </summary>
    public static string Name(GamePath path) => path.LocationId == LocationId.Game ? path.Path.ToString() : path.ToString();

    /// <summary>The path <see cref="Name"/> wrote: <c>{Location}/path</c> names a location, anything else is in the game folder.</summary>
    public static GamePath ParsePath(string text)
    {
        if (!text.StartsWith('{')) return new GamePath(LocationId.Game, text);

        var close = text.IndexOf("}/", StringComparison.Ordinal);
        if (close < 2) throw new UnwritableResolutionRefusedException($"'{text}' is not a path. Write {{Location}}/path for a file outside the game folder, for example {{Saves}}/slot1.sav.");
        return new GamePath(LocationId.From(text[1..close]), text[(close + 2)..]);
    }
}

/// <summary>What to do with an unwritable file, so the Apply stops asking for it.</summary>
public enum UnwritableResolution
{
    /// <summary>
    /// Accept the file the game folder has: back it up and make it the loadout's version of the path. Fits only
    /// files that are on disk.
    /// </summary>
    KeepDiskFile,

    /// <summary>Drop the row the loadout wants by recording a deletion. Fits only files that are not on disk.</summary>
    Forget,
}

/// <summary>What <see cref="ILoadoutSynchronizer.ResolveUnwritableFiles"/> did.</summary>
/// <param name="Resolved">Files that were settled.</param>
/// <param name="Skipped">Files that were asked for but are not unwritable, or that the resolution does not fit, each with the reason.</param>
public sealed record UnwritableResolutionResult(IReadOnlyList<GamePath> Resolved, IReadOnlyList<(GamePath Path, string Why)> Skipped);

/// <summary>Refuses a resolution that has nothing to settle.</summary>
public sealed class UnwritableResolutionRefusedException(string message) : UserFacingException(message)
{
    /// <inheritdoc />
    public override string Title => "Nothing was settled";
}
