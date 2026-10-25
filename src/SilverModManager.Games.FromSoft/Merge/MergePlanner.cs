using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Turns the merge index into merged bytes. It is the fold of erm's <c>conflicts.resolve</c> as a pure function:
/// nothing here reads the database, the file store or the disk, so what goes in is what decides what comes out.
/// </summary>
public static class MergePlanner
{
    /// <summary>
    /// Merges every path the index says will merge. The highest-priority contributor leads the fold, so it wins what a
    /// strategy settles by priority. A strategy that starts from the game's file puts that file first, so the output
    /// claims the installed build and a mod that ships a whole regulation contributes only what it authored.
    /// </summary>
    /// <param name="bytes">A contributor's file.</param>
    /// <param name="vanilla">The file a contributor branched from, asked per contributor and only for strategies that compare against it; null when none is available. Contributors built for different game builds have different answers.</param>
    /// <param name="gameBase">The installed game's own file for a path.</param>
    public static MergePlan Plan(
        MergePathIndex index,
        Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla,
        Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits)
        => Plan(index, bytes, vanilla, gameBase, traits, MergeStrategyBindings.For);

    // The bindings are a parameter so a test can fold with strategies that record what they were handed.
    internal static MergePlan Plan(
        MergePathIndex index,
        Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla,
        Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits,
        Func<MergeStrategy, MergeFold> bindings,
        RegulationRebase? rebase = null)
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
                merged.Add(Merge(candidate, strategy, bytes, vanilla, gameBase, traits, bindings(strategy), rebase));
            }
            catch (Exception exception) when (IsFormatFailure(exception))
            {
                return new MergePlan([], new MergeRefusal(candidate.Path, strategy, exception.Message, (exception as MergeException)?.Missing ?? []), notMergeable);
            }
        }
        return new MergePlan(merged, null, notMergeable);
    }

    private static MergedPath Merge(
        MergeCandidate candidate, MergeStrategy strategy, Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla, Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits, MergeFold fold, RegulationRebase? rebase)
    {
        var path = candidate.Path;
        var takesGameBase = MergeStrategies.TakesGameBase(strategy);
        var needsVanilla = MergeStrategies.NeedsVanilla(strategy);

        // One ancestor per contributor that is folded onto the accumulated side. Without the game's file the leader is the
        // accumulated side from the start and is never compared with anything.
        var firstFolded = takesGameBase ? 0 : 1;
        var ancestors = new byte[candidate.Contributors.Count][];
        Array.Fill(ancestors, Array.Empty<byte>());
        if (needsVanilla)
        {
            for (var i = firstFolded; i < ancestors.Length; i++)
            {
                var contributor = candidate.Contributors[i];
                ancestors[i] = vanilla(path, contributor)
                               ?? throw new MergeException($"{contributor.ModName} compares against the vanilla file it branched from, and none is available");
            }
        }

        var blobs = new List<byte[]>();
        var owners = new List<string>();
        if (takesGameBase)
        {
            blobs.Add(gameBase(path));
            owners.Add(MergeNames.Game);
        }
        foreach (var contributor in candidate.Contributors)
        {
            blobs.Add(bytes(contributor));
            owners.Add(contributor.ModName);
        }

        // The mods were built for an older build than the one installed: their rows are about to be moved onto the
        // game's file, which is only sound while the table layouts still agree.
        var rebasing = takesGameBase && rebase is not null && rebase.IsRebase(blobs[0], candidate.Contributors) ? rebase : null;
        if (rebasing is not null)
        {
            var problems = rebasing.Gate(blobs[0], candidate.Contributors.Select((c, i) => (c.ModName, blobs[i + 1])).ToArray());
            if (problems.Count > 0)
                throw new MergeException("the game's param layout moved, so mod rows can't be transplanted onto it:\n  "
                                         + string.Join("\n  ", problems)
                                         + "\nThose mods need updates built for this game version.");
        }

        // Past the first step the accumulated side is a merge led by the highest-priority mod, and that is who a note
        // names as the one whose content stayed.
        var leader = candidate.Contributors[0].ModName;
        var notes = new List<IMergeNote>();
        var result = blobs[0];
        // The accumulated side only starts as the game's file, so each step is told which of its rows still are.
        var game = takesGameBase ? new GameOrigin(blobs[0], traits) : null;
        var removedEdits = new List<(string Mod, RemovedRowEdit Row)>();
        for (var step = 0; step < blobs.Count - 1; step++)
        {
            var baseIsGame = step == 0 && takesGameBase;
            var ancestor = ancestors[step + 1 - (takesGameBase ? 1 : 0)];
            try
            {
                result = fold(
                    result, blobs[step + 1], ancestor, needsVanilla, baseIsGame,
                    baseIsGame ? MergeNames.Game : leader, owners[step + 1], notes, traits, game);
            }
            catch (RemovedRowEditsException removed)
            {
                // The accumulated side is left as it was so the later mods are still looked at and one refusal names them all.
                removedEdits.AddRange(removed.Rows.Select(r => (owners[step + 1], r)));
            }
        }
        if (removedEdits.Count > 0)
            throw new MergeException("these rows were removed by the installed game build and mods still edit them; those mods need an update built for it:\n  "
                                     + string.Join("\n  ", removedEdits.Select(e => $"{e.Mod}: {e.Row.EntryName} row {e.Row.RowId}")));

        if (rebasing is not null)
        {
            // By now the merged file is the only copy of every contributor's rows, and both ways a rebase goes wrong
            // (a merge still on the mods' old build, a dropped row) are invisible until the game is running.
            var problems = rebasing.Verify(result, blobs[0], candidate.Contributors.Select((c, i) => (c.ModName, blobs[i + 1], ancestors[i])).ToArray(),
                notes.OfType<RowConflict>().Where(c => c.KeptIsGame).ToArray());
            if (problems.Count > 0)
                throw new MergeException($"the merged {path} is not a faithful rebase onto the installed build:\n  "
                                         + string.Join("\n  ", problems)
                                         + "\nNothing was written; fix the mods and apply again.");
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
