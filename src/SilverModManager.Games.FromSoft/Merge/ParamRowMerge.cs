using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Folds one regulation onto another, given the regulation both branched from. Which rows to move is derived from the
/// data and never configured: a row the other side changed relative to vanilla is an edit, everything else is left
/// alone. That keeps a narrow patch narrow; copying a whole param across would revert every row the base authored in it.
/// </summary>
/// <remarks>
/// The rules are those of erm's <c>param_rows</c>, with one departure. Where erm refuses because both sides changed a
/// byte of one row to different values, this keeps the base's row whole and records a <see cref="RowConflict"/>. The
/// base is the higher-priority side (or the game's regulation on the first step of a fold), and priority is what the
/// loadout uses to settle a real conflict. The row is kept whole rather than spliced because a field spans several
/// bytes, and a splice of two edits to one field is a value neither author wrote.
/// <para>
/// Rows are compared as bytes. Two edits that touch different bytes of one multi-byte field still merge into a mixed
/// value; telling those apart needs the field layout, which a transplant otherwise does not need.
/// </para>
/// </remarks>
public static class ParamRowMerge
{
    /// <summary>
    /// Transplants <paramref name="other"/>'s param edits onto <paramref name="base"/>. The result is always repacked,
    /// even when nothing changed: the game's own regulation declares a compression window the mod loader cannot read,
    /// and packing again is what normalises it.
    /// </summary>
    /// <param name="baseIsGame">
    /// The base is the installed game's own regulation, not another mod's. It is the only thing that tells "the patch
    /// added this row" from "a mod folded earlier added it", and the two want opposite answers.
    /// </param>
    /// <param name="baseName">Who owns the base, for the notes; a generic name when null.</param>
    /// <param name="otherName">Who owns the other regulation, for the notes; a generic name when null.</param>
    /// <param name="game">
    /// The installed game's regulation, when the base is a fold that started from it. It tells a row the game owns from
    /// one an earlier mod changed, whichever step this is, and lets a mod's row edit be set against the patch's.
    /// </param>
    /// <exception cref="MergeException">The three regulations cannot be reconciled without guessing.</exception>
    /// <exception cref="RemovedRowEditsException">The other side edits rows the game removed, so there is nothing to carry them onto.</exception>
    public static byte[] Merge(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes, bool baseIsGame, string? baseName = null, string? otherName = null, GameOrigin? game = null)
        => Merge(@base, other, vanilla, traits, notes, baseIsGame, baseName, otherName, Regulation.Unpack, game);

    // The unpack seam exists so a test can count how often a regulation is decrypted.
    internal static byte[] Merge(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes, bool baseIsGame, string? baseName, string? otherName,
        Func<ReadOnlySpan<byte>, FromSoftFormatTraits, byte[]> unpack, GameOrigin? game = null)
    {
        var basePayload = unpack(@base, traits);
        if (baseIsGame) game?.Adopt(basePayload);
        var otherPayload = unpack(other, traits);
        var vanillaPayload = unpack(vanilla, traits);
        return Merge(basePayload, otherPayload, vanillaPayload, @base[..Regulation.IvSize].ToArray(), traits, notes, baseIsGame, baseName, otherName, game);
    }

    /// <summary>
    /// The merge over three regulations that are already decrypted. The base's payload is reused to write the result,
    /// so the AES pass over a shipped regulation, which takes seconds, is paid once per regulation.
    /// </summary>
    internal static byte[] Merge(
        byte[] basePayload, byte[] otherPayload, byte[] vanillaPayload, byte[] iv, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes, bool baseIsGame, string? baseName, string? otherName, GameOrigin? game = null)
    {
        var binder = Binders.For(traits);
        var baseRead = binder.Read(basePayload).ToList();
        var baseEntries = baseRead.ToDictionary(e => e.Id, e => e.Data);
        var entryNames = baseRead.ToDictionary(e => e.Id, e => e.Name[(e.Name.LastIndexOf('\\') + 1)..]);
        var otherEntries = binder.Read(otherPayload).ToDictionary(e => e.Id, e => e.Data);
        var vanillaEntries = binder.Read(vanillaPayload).ToDictionary(e => e.Id, e => e.Data);
        BinderEntrySets.RequireSameEntries(baseEntries.Keys, otherEntries.Keys, vanillaEntries.Keys, "regulations");

        var names = new Owners(
            baseName ?? (baseIsGame ? MergeNames.Game : MergeNames.PreferredMod),
            otherName ?? MergeNames.OtherMod);

        // Read lazily and only when a both-sides edit needs it: the stamp is not otherwise looked at.
        var ancestorVersion = new Lazy<string>(() => Regulation.Version(vanillaPayload));
        var removed = new List<RemovedRowEdit>();
        var replacements = new Dictionary<int, byte[]>();
        foreach (var (entryId, baseBlob) in baseEntries)
        {
            var otherBlob = otherEntries[entryId];
            var vanillaBlob = vanillaEntries[entryId];
            // These exits hold whatever the layout is, so a param neither side touched never has to parse. Some of
            // vanilla's own entries only parse after a tool re-saves them, and both mods carry the re-saved copy.
            if (baseBlob.AsSpan().SequenceEqual(otherBlob) || otherBlob.AsSpan().SequenceEqual(vanillaBlob)) continue;

            // Notes are held back until the entry merges, so a refused entry doesn't leave notes for edits that never landed.
            var entryNotes = new List<IMergeNote>();
            try
            {
                var merged = MergeParam(new ParamContext(entryId, entryNames[entryId], baseIsGame, names, game, ancestorVersion, removed), baseBlob, otherBlob, vanillaBlob, entryNotes);
                if (merged is not null) replacements[entryId] = merged;
            }
            catch (ParamException)
            {
                // A layout the reader refuses. If the base is byte-identical to vanilla, the other side's whole entry is
                // the only change there is and swapping it in loses nothing, though it is still a table taken
                // sight-unseen and gets named. Anything else is a real edit that can't be read.
                if (!baseBlob.AsSpan().SequenceEqual(vanillaBlob))
                    throw new MergeException($"entry {entryId} can't be parsed as a PARAM and both sides differ from vanilla; refusing to guess which edit to keep");
                replacements[entryId] = otherBlob;
                entryNotes = [new UnreadableParam(entryId)];
            }
            foreach (var note in entryNotes) notes?.Add(note);
        }
        if (removed.Count > 0) throw new RemovedRowEditsException(names.Dropped, removed);
        return Regulation.Pack(binder.Rebuild(basePayload, replacements), iv, traits);
    }

    private sealed record Owners(string Kept, string Dropped);

    /// <summary>What one param's merge needs to know beyond its three blobs.</summary>
    private sealed record ParamContext(
        int EntryId, string EntryName, bool BaseIsGame, Owners Owners, GameOrigin? Game, Lazy<string> AncestorVersion, List<RemovedRowEdit> Removed)
    {
        /// <summary>The base's copy of the row is still the game's own: the base is the game, or no earlier mod touched it.</summary>
        public bool BaseRowIsGames(int rowId, byte[] baseRow)
            => BaseIsGame || (Game?.Row(EntryId, rowId) is { } gameRow && ContentEqual(gameRow, baseRow));

        /// <summary>The game has no such row, so a base without it lost it in the patch rather than to an earlier mod.</summary>
        public bool GameLacksRow(int rowId)
            => BaseIsGame || (Game?.Rows(EntryId) is { } rows && !rows.ContainsKey(rowId));
    }

    /// <summary>Three-way row merge of one param. Null when the result would be the base as it is.</summary>
    private static byte[]? MergeParam(ParamContext context, byte[] baseBlob, byte[] otherBlob, byte[] vanillaBlob, List<IMergeNote> notes)
    {
        var entryId = context.EntryId;
        var b = Param.Read(baseBlob);
        var o = Param.Read(otherBlob);
        var v = Param.Read(vanillaBlob);

        var dupes = DuplicateIds(b).Union(DuplicateIds(o)).Union(DuplicateIds(v)).Order().ToArray();
        if (dupes.Length > 0)
        {
            // Everything below keys rows by id, which would collapse two distinct records into one and make a shadowed
            // edit compare equal to vanilla, dropped with the merge still reporting success. So decide the table
            // positionally: if either side is unchanged the answer needs no row lookup, and if both moved there is no
            // way to say which copy an edit belongs to.
            if (RowsIdentical(o, v)) return null;
            if (RowsIdentical(b, v)) return otherBlob;
            throw new MergeException(
                $"entry {entryId} carries row ids [{string.Join(", ", dupes.Take(5))}] more than once and both sides changed the param, so an edit can't be located by id; refusing to guess which copy moved");
        }

        var baseRows = b.Rows.ToDictionary(r => r.Id, r => r.Data);
        var otherRows = o.Rows.ToDictionary(r => r.Id, r => r.Data);
        var vanillaRows = v.Rows.ToDictionary(r => r.Id, r => r.Data);

        var overwrite = new Dictionary<int, byte[]>();
        var insert = new Dictionary<int, byte[]>();
        var delete = new HashSet<int>();
        foreach (var rowId in baseRows.Keys.Union(otherRows.Keys).Union(vanillaRows.Keys).Order())
        {
            baseRows.TryGetValue(rowId, out var bd);
            otherRows.TryGetValue(rowId, out var od);
            vanillaRows.TryGetValue(rowId, out var vd);

            var otherUntouched = (od is null && vd is null) || (od is not null && vd is not null && ContentEqual(od, vd));
            if (otherUntouched) continue;

            var baseUntouched = (bd is null && vd is null) || (bd is not null && vd is not null && ContentEqual(bd, vd));
            if (!baseUntouched)
            {
                // Both sides dropped it: agreement, and nothing to delete because the base no longer has it.
                if (bd is null && od is null) continue;
                if (bd is not null && od is not null && ContentEqual(bd, od)) continue;

                if (vd is null && bd is not null && od is not null)
                {
                    // Neither side inherited this row, so there is no ancestor to locate either edit against, and
                    // renumbering the loser would keep its bytes but lose its behaviour (a shop row is reached by id
                    // range). Someone has to win, and the loss is named.
                    if (context.BaseRowIsGames(rowId, bd))
                    {
                        // The game added one in a patch and a mod added a different one under the same id; the point of
                        // the stack is the mod's content. An earlier mod that left the game's row alone does not change that.
                        notes.Add(new RowCollision(entryId, rowId));
                        overwrite[rowId] = od;
                    }
                    else
                    {
                        // Fold order is priority order, so keeping the base's row is the answer the loadout already gave.
                        notes.Add(new RivalRow(entryId, rowId));
                    }
                    continue;
                }

                if (bd is null && vd is not null && od is not null && context.GameLacksRow(rowId))
                {
                    // The patch removed a row this side still edits. Collected rather than thrown so one refusal lists them all.
                    context.Removed.Add(new RemovedRowEdit(entryId, context.EntryName, rowId));
                    continue;
                }

                var merged = MergeRow(bd, od, vd, entryId, rowId);
                if (merged is null)
                {
                    var gameKept = bd is not null && context.BaseRowIsGames(rowId, bd);
                    var keptName = gameKept && !context.BaseIsGame ? MergeNames.Game : context.Owners.Kept;
                    notes.Add(new RowConflict(entryId, rowId, keptName, context.Owners.Dropped, gameKept));
                    continue;
                }
                if (bd is not null && merged.AsSpan().SequenceEqual(bd)) continue;   // the base already had everything the other side did
                overwrite[rowId] = merged;
                if (vd is not null && PatchChangedRowSinceAncestor(context, rowId, vd, out var gameVersion))
                    notes.Add(new CrossBuildRow(entryId, rowId, context.AncestorVersion.Value, gameVersion));
                continue;
            }

            // Only the other side moved, so its version wins, but a row can only be transplanted between files that
            // lay rows out the same way.
            if (od is null)
            {
                delete.Add(rowId);
                continue;
            }
            if (b.Stride != o.Stride)
            {
                if (Param.StridesComparable(b.Rows.Length, o.Rows.Length))
                    throw new MergeException(
                        $"entry {entryId} row {rowId} differs, but the two files disagree on row stride ({b.Stride} vs {o.Stride}); the row can't be transplanted without a paramdef to reinterpret it");
                // A one-row param's derived width folds in whatever the base keeps before its strings block (the head
                // of the type name), so a mismatch says nothing about where the fields sit. Refusing would block every
                // rebase onto the game's own regulation, where several shipped params measure wider than a mod's copy.
                od = FitToStride(od, bd, b.Stride, entryId, rowId);
            }
            if (baseRows.ContainsKey(rowId)) overwrite[rowId] = od;
            else insert[rowId] = od;
        }

        if (overwrite.Count == 0 && insert.Count == 0 && delete.Count == 0) return null;
        var patched = Param.PatchRows(b, overwrite, insert);
        if (delete.Count > 0) patched = Param.ReplaceRows(patched, patched.Rows.Where(r => !delete.Contains(r.Id)));
        return Param.Write(patched);
    }

    /// <summary>
    /// Whether the installed game changed this row after the build the other side was made for. Then the byte-level merge
    /// joins edits from two builds, and a patch that reused the id for something else would be spliced into the mod's
    /// meaning of it. Needs the game's regulation, so it is false where the base is a plain fold of mods.
    /// </summary>
    private static bool PatchChangedRowSinceAncestor(ParamContext context, int rowId, byte[] ancestorRow, out string gameVersion)
    {
        gameVersion = "";
        if (context.Game?.Row(context.EntryId, rowId) is not { } gameRow || ContentEqual(gameRow, ancestorRow)) return false;
        gameVersion = context.Game.Version;
        return !context.AncestorVersion.Value.Equals(gameVersion, StringComparison.Ordinal);
    }

    /// <summary>
    /// Three-way merge of one row at byte granularity, or null where both sides changed a byte to different values.
    /// Two mods editing one row is not automatically a conflict: a field is a contiguous byte range, so edits to
    /// disjoint bytes are edits to disjoint fields.
    /// </summary>
    private static byte[]? MergeRow(byte[]? @base, byte[]? other, byte[]? vanilla, int entryId, int rowId)
    {
        if (@base is null || other is null || vanilla is null)
        {
            // One side has no such row at all: it is new to both, or one side deleted what the other edited. Merging
            // bytes needs three versions to say who moved what, and picking a survivor without them is silent data loss.
            var absent = string.Join(" and ", new[] { ("base", @base), ("other", other), ("vanilla", vanilla) }.Where(s => s.Item2 is null).Select(s => s.Item1));
            throw new MergeException(
                $"entry {entryId} row {rowId}: both sides changed it, but the row is absent from {absent}; there is no common version to locate the edits against");
        }
        if (!(@base.Length == other.Length && other.Length == vanilla.Length))
            throw new MergeException(
                $"entry {entryId} row {rowId}: both sides changed it and the rows are different widths ({@base.Length}/{other.Length}/{vanilla.Length}), so the edits can't be located");

        // A clash discards the whole working copy, including other-side bytes already taken from earlier in the row:
        // keeping those would splice the loser's edits into a row that is supposed to stay the base's.
        var output = (byte[])@base.Clone();
        for (var i = 0; i < output.Length; i++)
        {
            if (other[i] == vanilla[i] || other[i] == @base[i]) continue;   // the other side didn't move this byte
            if (@base[i] != vanilla[i]) return null;
            output[i] = other[i];
        }
        return output;
    }

    /// <summary>
    /// Re-widens a transplanted row to the base file's row width. The excess beyond the row itself is whatever the base
    /// keeps between the last row and its strings block, so it comes from the base's own row and is never invented.
    /// Non-zero bytes past the base's width are content rather than padding, and losing them silently is exactly what
    /// the stride check exists to prevent.
    /// </summary>
    private static byte[] FitToStride(byte[] row, byte[]? baseRow, int stride, int entryId, int rowId)
    {
        if (row.Length == stride) return row;
        if (row.Length > stride)
        {
            if (row.AsSpan(stride).ContainsAnyExcept((byte)0))
                throw new MergeException(
                    $"entry {entryId} row {rowId} is {row.Length} bytes where the base lays rows out {stride} wide, and the extra bytes are not padding; the edit can't be fitted without a paramdef");
            return row[..stride];
        }

        var fitted = new byte[stride];
        row.CopyTo(fitted, 0);
        if (baseRow is not null && baseRow.Length > row.Length)
            baseRow.AsSpan(row.Length, Math.Min(stride, baseRow.Length) - row.Length).CopyTo(fitted.AsSpan(row.Length));
        return fitted;
    }

    /// <summary>
    /// Whether two rows hold the same content, tolerating a differing stride. Several shipped params derive a different row
    /// width per file because the bytes between the last row and the strings block fold into a one-row param's stride,
    /// so the same row can measure a few bytes different. Where the widths agree this is a plain comparison; where they
    /// don't, only the overlap is the row's content and the excess belongs to whichever file it came from. The
    /// tolerance is confined to the mismatched case, because applying it everywhere would hide a real edit in the tail.
    /// </summary>
    internal static bool ContentEqual(byte[] a, byte[] b)
    {
        var shared = Math.Min(a.Length, b.Length);
        return a.AsSpan(0, shared).SequenceEqual(b.AsSpan(0, shared));
    }

    /// <summary>
    /// The ids a param carries more than once. Legal and shipped: such ids are distinct records that happen to share a
    /// number, with different bytes in each copy.
    /// </summary>
    private static IEnumerable<int> DuplicateIds(ParamTable table)
    {
        var seen = new HashSet<int>();
        return table.Rows.Select(r => r.Id).Where(id => !seen.Add(id)).Distinct().ToArray();
    }

    /// <summary>
    /// Whether two params hold the same rows in the same order. Positional rather than keyed, because it is used exactly
    /// where an id doesn't identify a row.
    /// </summary>
    private static bool RowsIdentical(ParamTable x, ParamTable y)
        => x.Rows.Length == y.Rows.Length
           && x.Rows.Zip(y.Rows).All(p => p.First.Id == p.Second.Id && ContentEqual(p.First.Data, p.Second.Data));
}
