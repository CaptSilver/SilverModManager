using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>One merged path of a plan: the mods that provide it, highest priority first, and the notes the merge made.</summary>
public sealed record MergedEntry(string Path, IReadOnlyList<string> Mods, IReadOnlyList<IMergeNote> Notes);

/// <summary>
/// Turns a merge plan into Health Check cards: one for each note that records a choice, so a merge loss reads like every
/// other dropped item. Emitters are shared singletons, so the loadout is a parameter everywhere and nothing is kept
/// between calls.
/// </summary>
/// <typeparam name="TPlan">Whatever the game's merge produces; the subclass knows how to list its paths.</typeparam>
public abstract class AMergeDiagnosticsEmitter<TPlan>(ILogger logger) : ADroppedItemEmitter where TPlan : class
{
    protected ILogger Logger { get; } = logger;

    /// <summary>
    /// The plan, or null with the card that explains why there is none. A null plan and a null failure mean there is
    /// nothing to say (a game without a merge engine).
    /// </summary>
    protected abstract TPlan? TryPlan(Loadout.ReadOnly loadout, out Diagnostic? failure);

    protected abstract IEnumerable<MergedEntry> Merged(Loadout.ReadOnly loadout, TPlan plan);

    /// <summary>Dropped items that are not about a note of a merged path, such as a path the engine could not merge.</summary>
    protected virtual IEnumerable<DroppedItem> ExtraDropped(Loadout.ReadOnly loadout, TPlan plan) => [];

    /// <summary>Cards that are not dropped items at all.</summary>
    protected virtual IEnumerable<Diagnostic> Extra(Loadout.ReadOnly loadout, TPlan plan) => [];

    /// <summary>Whether a note gets a card: it records a loss, and is neither about the merge's source nor a detail.</summary>
    protected virtual bool RaisesCard(IMergeNote note) => note.Loss is not null && note is not (IVanillaSourceNote or IMergeDetailNote);

    /// <summary>
    /// The bare name of the page where the user reorders the mods that conflict. Every emitter names it: it differs per
    /// game and has to agree with the game's own card wording, which no default can know.
    /// </summary>
    protected abstract string ReorderPage(Loadout.ReadOnly loadout);

    /// <summary>
    /// The dropped item for the notes of one path that lose the same mod's change to the same other mod, so a mod that
    /// loses several hunks of a file gets one card listing all of them.
    /// </summary>
    protected virtual DroppedItem ToDroppedItem(Loadout.ReadOnly loadout, string path, IReadOnlyList<IMergeNote> notes)
    {
        var loss = notes[0].Loss ?? throw new InvalidOperationException($"The note '{notes[0].Describe()}' records no loss, so there is no dropped item to list");
        var ranges = notes.OfType<LinesKept>().OrderBy(k => k.FirstLine).Select(k => $"{k.FirstLine}–{k.LastLine}").ToArray();
        var lines = ranges.Length == 0 ? "" : $" lines {string.Join(", ", ranges)}";
        return new DroppedItem(
            $"{loss.DroppedMod}'s {(notes.Count > 1 ? "changes" : "change")} to `{path}`{lines}",
            loss.DroppedMod,
            $"both {loss.KeptMod} and {loss.DroppedMod} change it and {loss.KeptMod}'s change is used; " +
            $"to use {loss.DroppedMod}'s instead, {AMergeGame.ReorderAdvice(loss.DroppedMod, loss.KeptMod)}",
            ReorderPage(loadout));
    }

    /// <summary>
    /// The cards for the plan's notes: one per dropped item. A game with its own cards for its notes overrides it.
    /// </summary>
    protected virtual IAsyncEnumerable<Diagnostic> NoteCards(Loadout.ReadOnly loadout, TPlan plan, FrozenDictionary<GamePath, SyncNode> syncTree, CancellationToken cancellationToken)
        => Items(loadout, plan).Select(Card).ToAsyncEnumerable();

    private IEnumerable<DroppedItem> Items(Loadout.ReadOnly loadout, TPlan plan)
    {
        foreach (var entry in Merged(loadout, plan))
        {
            var losses = entry.Notes.Where(n => RaisesCard(n) && n.Loss is not null).GroupBy(n => n.Loss);
            foreach (var group in losses) yield return ToDroppedItem(loadout, entry.Path, group.ToArray());
        }

        foreach (var item in ExtraDropped(loadout, plan)) yield return item;
    }

    /// <summary>
    /// The <see cref="ADroppedItemEmitter"/> contract, for a caller that only knows dropped items: the same items the
    /// cards are made from (<see cref="Items"/>). <see cref="Diagnose(Loadout.ReadOnly, FrozenDictionary{GamePath, SyncNode}, CancellationToken)"/>
    /// does not go through it, because it also yields the plan's failure card and the cards that are not dropped items.
    /// </summary>
    protected override async IAsyncEnumerable<DroppedItem> Dropped(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        if (TryPlan(loadout, out _) is not { } plan) yield break;
        foreach (var item in Items(loadout, plan)) yield return item;
    }

    /// <summary>The plan's failure card, or the cards that are not dropped items, then the cards for the notes.</summary>
    public override async IAsyncEnumerable<Diagnostic> Diagnose(Loadout.ReadOnly loadout, FrozenDictionary<GamePath, SyncNode> syncTree, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        var plan = TryPlan(loadout, out var failure);
        if (failure is not null)
        {
            yield return failure;
            yield break;
        }

        if (plan is null) yield break;

        foreach (var card in Extra(loadout, plan)) yield return card;

        await foreach (var card in NoteCards(loadout, plan, syncTree, cancellationToken).WithCancellation(cancellationToken))
            yield return card;
    }
}
