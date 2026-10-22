using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// Turns the merge index into merged bytes. It is the fold of erm's <c>conflicts.resolve</c> as a pure function:
/// nothing here reads the database, the file store or the disk, so what goes in is what decides what comes out.
/// </summary>
public static class MergePlanner
{
    private const string GameName = "the game";

    /// <summary>
    /// Merges every path the index says will merge. The highest-priority contributor leads the fold, so it wins what a
    /// strategy settles by priority. A strategy that starts from the game's file puts that file first, so the output
    /// claims the installed build and a mod that ships a whole regulation contributes only what it authored.
    /// </summary>
    /// <param name="bytes">A contributor's file.</param>
    /// <param name="vanilla">The file the contributors branched from, resolved once per path and only for strategies that compare against it; null when none is available.</param>
    /// <param name="gameBase">The installed game's own file for a path.</param>
    public static MergePlan Plan(
        MergePathIndex index,
        Func<ProviderFile, byte[]> bytes,
        Func<string, IReadOnlyList<ProviderFile>, byte[]?> vanilla,
        Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits)
        => Plan(index, bytes, vanilla, gameBase, traits, MergeStrategyBindings.For);

    // The bindings are a parameter so a test can fold with strategies that record what they were handed.
    internal static MergePlan Plan(
        MergePathIndex index,
        Func<ProviderFile, byte[]> bytes,
        Func<string, IReadOnlyList<ProviderFile>, byte[]?> vanilla,
        Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits,
        Func<MergeStrategy, MergeFold> bindings)
    {
        var notMergeable = index.Candidates.Where(c => !c.Outcome.Merges).ToArray();
        var merged = new List<MergedPath>();

        // Everything is computed before anything is returned: a failure on a later path must leave no earlier output.
        foreach (var candidate in index.Candidates.Where(c => c.Outcome.Merges))
        {
            var strategy = candidate.Strategy
                           ?? throw new InvalidOperationException($"{candidate.Path} is marked as merging but has no strategy");
            try
            {
                merged.Add(Merge(candidate, strategy, bytes, vanilla, gameBase, traits, bindings(strategy)));
            }
            catch (Exception exception) when (IsFormatFailure(exception))
            {
                return new MergePlan([], new MergeRefusal(candidate.Path, strategy, exception.Message), notMergeable);
            }
        }
        return new MergePlan(merged, null, notMergeable);
    }

    private static MergedPath Merge(
        MergeCandidate candidate, MergeStrategy strategy, Func<ProviderFile, byte[]> bytes,
        Func<string, IReadOnlyList<ProviderFile>, byte[]?> vanilla, Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits, MergeFold fold)
    {
        var path = candidate.Path;
        var takesGameBase = MergeStrategies.TakesGameBase(strategy);
        var needsVanilla = MergeStrategies.NeedsVanilla(strategy);

        byte[]? ancestor = null;
        if (needsVanilla)
        {
            ancestor = vanilla(path, candidate.Contributors)
                       ?? throw new MergeException("compares both mods against the vanilla file they branched from, and none is available");
        }

        var blobs = new List<byte[]>();
        var owners = new List<string>();
        if (takesGameBase)
        {
            blobs.Add(gameBase(path));
            owners.Add(GameName);
        }
        foreach (var contributor in candidate.Contributors)
        {
            blobs.Add(bytes(contributor));
            owners.Add(contributor.ModName);
        }

        // Past the first step the accumulated side is a merge led by the highest-priority mod, and that is who a note
        // names as the one whose content stayed.
        var leader = candidate.Contributors[0].ModName;
        var notes = new List<IMergeNote>();
        var result = blobs[0];
        for (var step = 0; step < blobs.Count - 1; step++)
        {
            var baseIsGame = step == 0 && takesGameBase;
            result = fold(
                result, blobs[step + 1], ancestor ?? [], ancestor is not null, baseIsGame,
                baseIsGame ? GameName : leader, owners[step + 1], notes, traits);
        }

        return new MergedPath(path, strategy, candidate.Contributors, result, notes, VanillaSourceOf(strategy));
    }

    private static string VanillaSourceOf(MergeStrategy strategy)
    {
        if (!MergeStrategies.NeedsVanilla(strategy)) return string.Empty;
        return MergeStrategies.TakesGameBase(strategy)
            ? "the vanilla regulation the mods were built against, from a kept baseline, a vanilla dump in the library or the installed game"
            : "the vanilla copy of the file in a vanilla dump in the library";
    }

    // A strategy refuses with its own exception, and the readers under it add theirs; all of them mean this path
    // cannot be merged, and none of them is a bug in the planner.
    private static bool IsFormatFailure(Exception exception) => exception is
        MergeException or ParamException or TpfException or EsdMergeException or EsdException or FmgException
        or DcxException or RegulationException or Bnd3Exception or Bnd4Exception or AesException or KrakenException;
}
