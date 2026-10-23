using SilverModManager.Games.FromSoft.Formats;
using EsdFormat = SilverModManager.Games.FromSoft.Formats.Esd;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Merges two talk archives (.talkesdbnd.dcx) that both claim the same file. The container is handled here and the
/// state machines in <see cref="EsdMerge"/>. Both read and rebuild through the game's binder, so a BND3 game merges the
/// same way a BND4 game does.
/// </summary>
public static class EsdArchiveMerge
{
    /// <summary>
    /// Merges two archives against the vanilla both branched from. An entry only one side changed is swapped whole,
    /// which is how two mods editing different machines of one archive cross without any graph work. Only an entry
    /// both sides changed reaches <see cref="EsdMerge"/>, and base wins whatever it cannot keep from both.
    /// </summary>
    /// <param name="notes">Receives whatever the graph merge could not carry across cleanly, entry by entry; null collects nothing.</param>
    /// <exception cref="MergeException">The three archives hold different entries.</exception>
    /// <exception cref="EsdMergeException">An entry's merge, or the file written for it, does not hold together.</exception>
    public static byte[] ThreeWay(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes = null) =>
        ThreeWay(@base, other, vanilla, traits, notes, EsdFormat.Write);

    // The writer is a parameter so a test can hand in one that emits a broken file: the check below has to be reached
    // through bytes, and no real input makes the real writer produce a bad one.
    internal static byte[] ThreeWay(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes, Func<EsdFile, byte[]> write)
    {
        var binder = Binders.For(traits);
        var baseRaw = Dcx.Read(@base, traits);
        var baseEntries = binder.Read(baseRaw).ToDictionary(e => e.Id, e => e.Data);
        var otherEntries = binder.Read(Dcx.Read(other, traits)).ToDictionary(e => e.Id, e => e.Data);
        var vanillaEntries = binder.Read(Dcx.Read(vanilla, traits)).ToDictionary(e => e.Id, e => e.Data);

        // Without vanilla to compare an entry against, "only the other side moved" cannot be told from "base authored
        // this and vanilla never carried it", and guessing wrong throws away the preferred mod's own content.
        BinderEntrySets.RequireSameEntries(baseEntries.Keys, otherEntries.Keys, vanillaEntries.Keys, "archives");

        var replacements = new Dictionary<int, byte[]>();
        foreach (var (id, baseBlob) in baseEntries)
        {
            var otherBlob = otherEntries[id];
            var vanillaBlob = vanillaEntries[id];
            if (otherBlob.AsSpan().SequenceEqual(baseBlob) || otherBlob.AsSpan().SequenceEqual(vanillaBlob)) continue; // base's own edit, or nobody touched it
            if (baseBlob.AsSpan().SequenceEqual(vanillaBlob))
            {
                replacements[id] = otherBlob; // only the other side moved
                continue;
            }

            var (merged, entryNotes) = EsdMerge.Merge(EsdFormat.Read(baseBlob), EsdFormat.Read(otherBlob), EsdFormat.Read(vanillaBlob));
            var blob = write(merged);

            // Checks the bytes, not the graph EsdMerge already checked: reading them back puts the writer under the
            // same invariants as everything else, and the reader adds its own on the way through. Nothing else reads
            // back what it wrote, so without this the first reader of a merged file is the game.
            EsdCheck.Check(EsdFormat.Read(blob));
            replacements[id] = blob;
            if (notes is not null)
            {
                foreach (var note in entryNotes) notes.Add(note);
            }
        }
        return Dcx.Write(binder.Rebuild(baseRaw, replacements), traits.PackedWrite);
    }
}
