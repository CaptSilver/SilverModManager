namespace SilverModManager.Abstractions.Games.Merge.Tests.Fixtures;

/// <summary>The text merge, counting how many paths it was asked to fold.</summary>
internal sealed class CountingStrategy : IMergeStrategy
{
    public int Folds;

    public string Name => TextThreeWayMerge.Instance.Name;

    public MergeFoldResult Fold(IReadOnlyList<MergeInput> contributors, ReadOnlySpan<byte> vanilla, bool hasVanilla, string path, ICollection<IMergeNote> notes)
    {
        Interlocked.Increment(ref Folds);
        return TextThreeWayMerge.Instance.Fold(contributors, vanilla, hasVanilla, path, notes);
    }
}
