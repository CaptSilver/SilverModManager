namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>One contributor of a contested path, highest rank first in the list the engine hands <see cref="IMergeStrategy.Fold"/>.</summary>
public readonly record struct MergeInput(byte[] Bytes, string ModName);

/// <summary>What a fold produced. <paramref name="Hunks"/> is how many hunks the fold walked, for the Trace line; -1 when the strategy has no such notion.</summary>
public sealed record MergeFoldResult(byte[] Bytes, int Hunks);

/// <summary>How the contributors of one contested path become the file the apply writes.</summary>
public interface IMergeStrategy
{
    /// <summary>The spelling the page and CLI show, e.g. "text-3way".</summary>
    string Name { get; }

    /// <summary>
    /// Folds every contributor of one path (rank descending) into the bytes the apply writes. A nullable
    /// ReadOnlySpan does not compile, so "no trusted base" is <paramref name="hasVanilla"/> == false with an empty
    /// vanilla. Throws <see cref="MergeInputException"/> on unreadable input. The strategy owns the whole fold so it
    /// can say which contributor each kept piece came from; every loss note names real mods, never a stand-in.
    /// </summary>
    MergeFoldResult Fold(IReadOnlyList<MergeInput> contributors, ReadOnlySpan<byte> vanilla, bool hasVanilla, string path, ICollection<IMergeNote> notes);
}

/// <summary>A contributor's bytes cannot be merged by the strategy (for the text merge: not UTF-8). The message names the file's owner and is fit to show.</summary>
public sealed class MergeInputException(string message) : Exception(message);
