using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Merges two binders that both claim the same file, member by member, for a binder whose members are opaque to the
/// merge: an equipment part holds a texture archive, models and animation sets, and the member is the unit, never the
/// files inside a nested binder. Reads and rebuilds through the game's binder, so a BND3 game merges like a BND4 game.
/// </summary>
public static class BinderMemberMerge
{
    /// <summary>
    /// Merges two bundles against the vanilla both branched from. A member only one side changed goes to that side, a
    /// member both changed is merged by <see cref="MemberPolicy"/>: texture archives texture by texture, everything
    /// else stays as base has it. Members are identified by id, which is what the game looks them up by. Base's member
    /// list is the result's: a member base or the other deleted or added is merged as a value (absent or present), and a
    /// bundle that would need a member added or removed is refused.
    /// </summary>
    /// <param name="notes">
    /// Receives what was done member by member. It is also read: at a conflict the kept mod is the one the latest
    /// <see cref="MemberTaken"/> for that member names, so a notes list carried through every step of a fold credits
    /// the mod that actually supplied the copy, not whoever leads. Null collects and reads nothing.
    /// </param>
    /// <param name="baseName">Who owns base, for the notes; a generic name when null.</param>
    /// <param name="otherName">Who owns the other bundle, for the notes; a generic name when null.</param>
    /// <returns>
    /// The base bytes untouched when no member is replaced, so an unchanged file is not recompressed and a Kraken bundle
    /// stays Kraken; otherwise base rebuilt with the replaced members in the game's packed format.
    /// </returns>
    /// <exception cref="MergeException">The other bundle needs a member added or removed, the bundles name or store a member differently, or repeat an id.</exception>
    public static byte[] ThreeWay(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes = null, string? baseName = null, string? otherName = null)
    {
        baseName ??= MergeNames.PreferredMod;
        otherName ??= MergeNames.OtherMod;
        var binder = Binders.For(traits);
        var baseRaw = Dcx.Read(@base, traits);
        var otherRaw = Dcx.Read(other, traits);
        var vanillaRaw = Dcx.Read(vanilla, traits);

        var baseSide = Side.Read(binder, baseRaw, traits, baseName);
        var otherSide = Side.Read(binder, otherRaw, traits, otherName);
        var vanillaSide = Side.Read(binder, vanillaRaw, traits, "the game's own copy");

        RequireRebuildable(baseSide, otherSide, vanillaSide);
        RequireSameLayout(baseSide, otherSide, vanillaSide);

        var suppliers = LatestTakers(notes);
        var replacements = new Dictionary<int, byte[]>();
        var added = new List<IMergeNote>();

        // A member the other lacks or base lacks is a value like any other: base's list is the rebuild's list, so what
        // base holds or lacks stands and the other's edit to a member base deleted is dropped.
        foreach (var (id, theirMember) in otherSide.Entries)
        {
            if (baseSide.Entries.ContainsKey(id) || !vanillaSide.Entries.TryGetValue(id, out var gone)) continue;
            if (!theirMember.Data.AsSpan().SequenceEqual(gone.Data))
                added.Add(new MemberDeletionKept(gone.Name, baseName, otherName, KeptDeletion: true));
        }

        foreach (var (id, own) in baseSide.Entries)
        {
            if (!otherSide.Entries.TryGetValue(id, out var theirMember))
            {
                // Vanilla holding it means the other deleted it while base changed it (an unchanged copy was refused above),
                // and base's copy stays. Vanilla lacking it is base's own addition, which the other never saw.
                if (vanillaSide.Entries.ContainsKey(id))
                    added.Add(new MemberDeletionKept(own.Name, suppliers.GetValueOrDefault(own.Name, baseName), otherName, KeptDeletion: false));
                continue;
            }

            var theirs = theirMember.Data;
            if (!vanillaSide.Entries.TryGetValue(id, out var vanillaMember))
            {
                // Both added it. Same bytes is nothing to say; different bytes is a plain conflict.
                if (!theirs.AsSpan().SequenceEqual(own.Data))
                    added.Add(new MemberKept(own.Name, suppliers.GetValueOrDefault(own.Name, baseName), otherName));
                continue;
            }

            var ancestor = vanillaMember.Data;
            if (theirs.AsSpan().SequenceEqual(own.Data) || theirs.AsSpan().SequenceEqual(ancestor)) continue; // base's own edit, or nobody touched it
            if (own.Data.AsSpan().SequenceEqual(ancestor))
            {
                replacements[id] = theirs;                                                                    // only the other side moved
                added.Add(new MemberTaken(own.Name, otherName));
                suppliers[own.Name] = otherName;
                continue;
            }

            var keptName = suppliers.GetValueOrDefault(own.Name, baseName);
            string? unreadable = null;
            if (MemberPolicy.For(own.Name) == MemberMerge.Texture
                && TryMergeTextures(own.Data, theirs, ancestor, baseName, otherName, keptName, out var textures, out var textureNotes, out unreadable))
            {
                if (!textures.AsSpan().SequenceEqual(own.Data)) replacements[id] = textures;
                added.AddRange(textureNotes);
                continue;
            }

            added.Add(new MemberKept(own.Name, keptName, otherName, unreadable));
        }

        // A model whose animations came from the mod it lost to is a pairing neither mod made.
        for (var i = 0; i < added.Count; i++)
        {
            if (added[i] is MemberKept { Details: null } kept && MemberPolicy.Noun(kept.Member) == "the model"
                && suppliers.Any(s => s.Value == kept.DroppedMod && MemberPolicy.Noun(s.Key) == "the animations"))
            {
                added[i] = kept with
                {
                    Details = $"{kept.KeptMod}'s model is used with {kept.DroppedMod}'s animations, so the model and its animations now come from different mods",
                };
            }
        }

        if (notes is not null)
        {
            foreach (var note in added) notes.Add(note);
        }

        if (replacements.Count == 0) return @base.ToArray();
        return Dcx.Write(binder.Rebuild(baseRaw, replacements), traits.PackedWrite);
    }

    // Latest take per member name across the notes a fold has gathered so far.
    private static Dictionary<string, string> LatestTakers(ICollection<IMergeNote>? notes)
    {
        var takers = new Dictionary<string, string>();
        if (notes is null) return takers;
        foreach (var taken in notes.OfType<MemberTaken>()) takers[taken.Member] = taken.FromMod;
        return takers;
    }

    // A TPF the reader or writer refuses, or one that repeats a texture name, must not sink the members that merged
    // cleanly beside it: the member falls back to the opaque rule and the reader's message rides on the note.
    private static bool TryMergeTextures(
        byte[] own, byte[] theirs, byte[] ancestor, string baseName, string otherName, string keptName,
        out byte[] merged, out List<IMergeNote> textureNotes, out string? unreadable)
    {
        textureNotes = [];
        try
        {
            merged = TpfMerge.ThreeWay(own, theirs, ancestor, textureNotes, baseName, otherName, keptName);
            unreadable = null;
            return true;
        }
        catch (TpfException e)
        {
            merged = own;
            textureNotes = [];
            unreadable = e.Message;
            return false;
        }
    }

    // The rebuild keeps base's member list, so the other bundle may not need a member added or removed to carry its edits.
    private static void RequireRebuildable(Side @base, Side other, Side vanilla)
    {
        foreach (var (id, theirs) in other.Entries.OrderBy(e => e.Key))
        {
            if (!@base.Entries.ContainsKey(id) && !vanilla.Entries.ContainsKey(id))
                throw new MergeException($"{other.Owner} adds member {id} ({theirs.Name}) that neither {@base.Owner} nor {vanilla.Owner} holds, and a merged bundle cannot add a member");
        }

        foreach (var (id, own) in @base.Entries.OrderBy(e => e.Key))
        {
            if (!other.Entries.ContainsKey(id) && vanilla.Entries.TryGetValue(id, out var ancestor) && own.Data.AsSpan().SequenceEqual(ancestor.Data))
                throw new MergeException($"{other.Owner} dropped member {id} ({own.Name}) that {@base.Owner} still carries as {vanilla.Owner} has it, and a merged bundle cannot remove a member");
        }
    }

    // Compares a member's name and storage wherever two sides both hold it.
    private static void RequireSameLayout(Side @base, Side other, Side vanilla)
    {
        foreach (var (id, own) in @base.Entries)
        {
            if (other.Entries.TryGetValue(id, out var theirs))
            {
                if (own.Name != theirs.Name)
                    throw new MergeException($"member {id} is {own.Name} in {@base.Owner} but {theirs.Name} in {other.Owner}; the bundles are laid out differently");
                // A flag such as compressed-inside-binder changes what the bytes mean, and a rebuild carries base's.
                if (own.Flags != theirs.Flags)
                    throw new MergeException($"member {id} ({own.Name}) is stored differently in {@base.Owner} (0x{own.Flags:X2}) and {other.Owner} (0x{theirs.Flags:X2})");
            }

            if (!vanilla.Entries.TryGetValue(id, out var ancestor)) continue;
            var holders = theirs is null ? @base.Owner : $"{@base.Owner} and {other.Owner}";
            if (own.Name != ancestor.Name)
                throw new MergeException($"member {id} is {own.Name} in {holders} but {ancestor.Name} in {vanilla.Owner}; the bundles are laid out differently");
            if (own.Flags != ancestor.Flags)
                throw new MergeException($"member {id} ({own.Name}) is stored differently in {holders} (0x{own.Flags:X2}) than in {vanilla.Owner} (0x{ancestor.Flags:X2})");
        }
    }

    private sealed record Member(string Name, byte[] Data, byte Flags);

    private sealed record Side(string Owner, Dictionary<int, Member> Entries)
    {
        public static Side Read(IBinder binder, byte[] raw, FromSoftFormatTraits traits, string owner)
        {
            var entries = binder.Read(raw);
            var flags = traits.Binder == BinderFormat.Bnd4 ? Bnd4.EntryFlags(raw) : Bnd3.EntryFlags(raw);
            var byId = new Dictionary<int, Member>();
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                if (!byId.TryAdd(e.Id, new Member(e.Name, e.Data, flags[i])))
                    throw new MergeException($"{owner}'s bundle holds member id {e.Id} more than once ({byId[e.Id].Name} and {e.Name}), so a merge by id cannot tell the copies apart");
            }
            return new Side(owner, byId);
        }
    }
}
