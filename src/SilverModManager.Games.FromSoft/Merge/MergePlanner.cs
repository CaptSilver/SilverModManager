using SilverModManager.Abstractions.Games.Merge;
using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Turns the merge index into merged bytes. It is the fold of erm's <c>conflicts.resolve</c> as a pure function:
/// nothing here reads the database, the file store or the disk, so what goes in is what decides what comes out.
/// </summary>
public static class MergePlanner
{
    /// <summary>
    /// Merges every path the index says will merge. A path other than the regulation whose merge fails is left unmerged
    /// (<see cref="MergePlan.Unmerged"/>) and the rest still merge; the regulation failing refuses the plan. The
    /// highest-priority contributor leads the fold, so it wins what a strategy settles by priority. A strategy that
    /// starts from the game's file puts that file first, so the output claims the installed build and a mod that ships a whole regulation contributes only what it authored.
    /// </summary>
    /// <param name="bytes">A contributor's file.</param>
    /// <param name="vanilla">The file a contributor branched from, asked per contributor and only for strategies that compare against it; null when none is available. Contributors built for different game builds have different answers.</param>
    /// <param name="gameBase">The installed game's own file for a path.</param>
    /// <param name="choices">The loadout's pinned merge winners by path; a pin decides a contested item the load order would otherwise decide.</param>
    public static MergePlan Plan(
        MergePathIndex index,
        Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla,
        Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits,
        IReadOnlyDictionary<string, PathChoices>? choices = null)
        => Plan(index, bytes, vanilla, gameBase, traits, MergeStrategyBindings.For, choices: choices);

    // The bindings are a parameter so a test can fold with strategies that record what they were handed.
    internal static MergePlan Plan(
        MergePathIndex index,
        Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla,
        Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits,
        Func<MergeStrategy, MergeFold> bindings,
        RegulationRebase? rebase = null,
        IReadOnlyDictionary<string, PathChoices>? choices = null)
    {
        var notMergeable = index.Candidates.Where(c => !c.Outcome.Merges).ToArray();
        var merged = new List<MergedPath>();
        var unmerged = new List<MergeCandidate>();

        // Everything is computed before anything is returned: a failure on a later path must leave no earlier output.
        foreach (var candidate in index.Candidates.Where(c => c.Outcome.Merges))
        {
            var strategy = candidate.Strategy
                           ?? throw new InvalidOperationException($"{candidate.Path} is marked as merging but has no strategy");
            try
            {
                merged.Add(MergeInSomeOrder(candidate, strategy, bytes, vanilla, gameBase, traits, bindings(strategy), rebase, choices?.GetValueOrDefault(candidate.Path)));
            }
            catch (Exception exception) when (MergeFailures.IsInputProblem(exception))
            {
                if (MergeStrategies.RefusesWhenItFails(strategy))
                    return new MergePlan([], new MergeRefusal(candidate.Path, strategy, exception.Message, (exception as MergeException)?.Missing ?? []), notMergeable);

                unmerged.Add(candidate with { Outcome = MergeOutcome.NotMergeable(MergeOutcome.MergeFailed(exception.Message)), Winner = index.WinnerOf(candidate), VanillaSource = VanillaSourceOf(strategy) });
            }
        }
        return new MergePlan(merged, null, notMergeable, unmerged);
    }

    // A merge whose result depends on who leads is tried with each contributor as the base before it is given up on.
    // The base is the only thing that moves: the others follow in priority order, so the same inputs always give the same
    // file. The error that is reported is the priority order's, since that is the order the user set up.
    private static MergedPath MergeInSomeOrder(
        MergeCandidate candidate, MergeStrategy strategy, Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla, Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits, MergeFold fold, RegulationRebase? rebase, PathChoices? choices)
    {
        try
        {
            return Merge(candidate, strategy, bytes, vanilla, gameBase, traits, fold, rebase, choices);
        }
        catch (Exception first) when (MergeFailures.IsInputProblem(first) && MergeStrategies.IsOrderSensitive(strategy) && candidate.Contributors.Count > 1)
        {
            for (var lead = 1; lead < candidate.Contributors.Count; lead++)
            {
                var order = candidate.Contributors.Where((_, i) => i == lead)
                    .Concat(candidate.Contributors.Where((_, i) => i != lead)).ToArray();
                try
                {
                    var merged = Merge(candidate with { Contributors = order }, strategy, bytes, vanilla, gameBase, traits, fold, rebase, choices);
                    return merged with
                    {
                        Contributors = candidate.Contributors,
                        Notes = [new MergeBaseChanged(order[0].ModName, candidate.Contributors[0].ModName, first.Message), .. merged.Notes],
                    };
                }
                catch (Exception other) when (MergeFailures.IsInputProblem(other))
                {
                    // Not this one either; the next contributor gets the lead.
                }
            }
            throw;
        }
    }

    private static MergedPath Merge(
        MergeCandidate candidate, MergeStrategy strategy, Func<ProviderFile, byte[]> bytes,
        Func<string, ProviderFile, byte[]?> vanilla, Func<string, byte[]> gameBase,
        FromSoftFormatTraits traits, MergeFold fold, RegulationRebase? rebase, PathChoices? configured)
    {
        var path = candidate.Path;
        // A retried lead folds again from the start, so each attempt records what the pins did on its own.
        var choices = configured?.Fresh();
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
                var before = notes.Count;
                result = fold(
                    result, blobs[step + 1], ancestor, needsVanilla, baseIsGame,
                    baseIsGame ? MergeNames.Game : leader, owners[step + 1], notes, traits, game, choices);
                if (!baseIsGame)
                    NameRealWinners(notes, before, leader, owners, blobs, step, takesGameBase, ancestor, needsVanilla, fold, traits, game);
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

        var stale = choices is null ? [] : ApplyChoices(notes, choices);
        return new MergedPath(path, strategy, candidate.Contributors, result, notes, VanillaSourceOf(strategy), stale);
    }

    /// <summary>
    /// Past the second contributor the accumulated side is a merge, and a fold names the mod that leads it as the owner of
    /// whatever stayed. The change that stayed may be a lower mod's, when the leader never touched it. This finds the
    /// highest-priority earlier mod that conflicts with the next one the same way (the first mod to hold the
    /// disputed item) and names that mod instead.
    /// </summary>
    private static void NameRealWinners(
        List<IMergeNote> notes, int from, string leader, List<string> owners, List<byte[]> blobs, int step, bool takesGameBase,
        byte[] ancestor, bool needsVanilla, MergeFold fold, FromSoftFormatTraits traits, GameOrigin? game)
    {
        var offset = takesGameBase ? 1 : 0;
        var earlier = step + 1 - offset; // contributors before the one this step folds in
        if (earlier < 2) return;

        // Each pairwise fold decodes, merges and re-encodes the whole file, so it runs at most once per earlier mod
        // however many notes ask about it, and only when a note needs it.
        var pairwise = new HashSet<IMergeNote>?[earlier];
        HashSet<IMergeNote> NotesOf(int k)
        {
            if (pairwise[k] is { } cached) return cached;

            var scratch = new List<IMergeNote>();
            try
            {
                fold(blobs[k + offset], blobs[step + 1], ancestor, needsVanilla, false, leader, owners[step + 1], scratch, traits, game, null);
            }
            catch (Exception e) when (MergeFailures.IsInputProblem(e) || e is RemovedRowEditsException)
            {
                scratch.Clear();
            }
            return pairwise[k] = [..scratch];
        }

        for (var i = from; i < notes.Count; i++)
        {
            if (notes[i] is not IWinnerNote { Winner: var winner } note || winner != leader) continue;

            // The leader (k = 0) is asked too: when it is the real winner the note stays as it is.
            for (var k = 0; k < earlier; k++)
            {
                if (!NotesOf(k).Contains(note)) continue;

                if (k > 0) notes[i] = note.WithWinner(owners[k + offset]);
                break;
            }
        }
    }

    /// <summary>
    /// Whether a pin took effect is only known once the whole fold has run (with A above B above C and C pinned, the
    /// contest of A and B is settled before C is folded in), so the notes are relabelled here. A pin is live when its mod
    /// took the item at its own step or already is the winner some note names; every note of that contest then names
    /// the pinned mod. Any other pin names a mod that never contested the item and is ignored.
    /// </summary>
    private static List<(string Key, string Reason)> ApplyChoices(List<IMergeNote> notes, PathChoices choices)
    {
        var stale = new List<(string Key, string Reason)>(choices.Stale);
        foreach (var (key, winner) in choices.All)
        {
            var live = choices.Owner(key) is not null
                       || notes.OfType<IWinnerNote>().Any(n => n.ChoiceKey == key && n.Winner == winner);
            if (!live)
            {
                stale.Add((key, $"{winner} does not edit {key}"));
                continue;
            }

            for (var i = 0; i < notes.Count; i++)
            {
                if (notes[i] is IChoosableNote note && note.ChoiceKey == key) notes[i] = note.WithChosen(winner);
            }
        }
        return stale;
    }

    private static string VanillaSourceOf(MergeStrategy strategy)
    {
        if (!MergeStrategies.NeedsVanilla(strategy)) return string.Empty;
        return MergeStrategies.TakesGameBase(strategy)
            ? "the vanilla regulation the mods were built against, from a kept baseline, a vanilla dump in the library or the installed game"
            : "the vanilla copy of the file in a vanilla dump in the library or in the game's own archives";
    }
}
