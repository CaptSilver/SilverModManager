namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>A note and the merged path it is about; one apply merges several paths and a bare note would not say which.</summary>
public sealed record PathNote(string Path, IMergeNote Note);

/// <summary>One path the plan merges.</summary>
/// <param name="Path">The package-relative path the merged file is mounted at.</param>
/// <param name="Strategy">How the contributors were combined.</param>
/// <param name="Contributors">The files that fed the merge, the highest priority first, which is the order they were folded in.</param>
/// <param name="Bytes">The merged file.</param>
/// <param name="Notes">What the merge kept, dropped or took whole.</param>
/// <param name="VanillaSource">A sentence naming where the ancestor came from; empty for the unions, which compare nothing.</param>
public sealed record MergedPath(
    string Path,
    MergeStrategy Strategy,
    IReadOnlyList<ProviderFile> Contributors,
    byte[] Bytes,
    IReadOnlyList<IMergeNote> Notes,
    string VanillaSource);

/// <summary>Why the plan has no merged files: one path could not be merged, so none was written.</summary>
public sealed record MergeRefusal(string Path, MergeStrategy Strategy, string Reason)
{
    public string Describe() => $"{Path} ({MergeStrategies.Name(Strategy)}): {Reason}";
}

/// <summary>
/// What an apply would merge. Either every mergeable path has its merged bytes, or the plan carries the one
/// <see cref="Refusal"/> and no bytes at all: a later path failing must not leave an earlier one half done.
/// </summary>
/// <param name="Merged">Every merged path, sorted by path; empty when refused.</param>
/// <param name="Refusal">The path that stopped the plan, if any.</param>
/// <param name="NotMergeable">The index's candidates the engine will not merge, passed through for the reports.</param>
public sealed record MergePlan(
    IReadOnlyList<MergedPath> Merged,
    MergeRefusal? Refusal,
    IReadOnlyList<MergeCandidate> NotMergeable)
{
    /// <summary>Every merged path's notes, each tagged with its path.</summary>
    public IEnumerable<PathNote> Notes => Merged.SelectMany(m => m.Notes.Select(n => new PathNote(m.Path, n)));
}
