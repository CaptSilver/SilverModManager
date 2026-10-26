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
    /// else stays as base has it. Members are identified by id, which is what the game looks them up by.
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
    /// <exception cref="MergeException">The bundles hold different members, name or store a member differently, or repeat an id.</exception>
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

        BinderEntrySets.RequireSameEntries(baseSide.Entries.Keys, otherSide.Entries.Keys, vanillaSide.Entries.Keys, "bundles");
        RequireSameLayout(baseSide, otherSide, vanillaSide);

        var suppliers = LatestTakers(notes);
        var replacements = new Dictionary<int, byte[]>();
        var added = new List<IMergeNote>();

        foreach (var (id, own) in baseSide.Entries)
        {
            var theirs = otherSide.Entries[id].Data;
            var ancestor = vanillaSide.Entries[id].Data;
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

    private static void RequireSameLayout(Side @base, Side other, Side vanilla)
    {
        foreach (var (id, own) in @base.Entries)
        {
            var theirs = other.Entries[id];
            var ancestor = vanilla.Entries[id];
            if (own.Name != theirs.Name)
                throw new MergeException($"member {id} is {own.Name} in {@base.Owner} but {theirs.Name} in {other.Owner}; the bundles are laid out differently");
            if (own.Name != ancestor.Name)
                throw new MergeException($"member {id} is {own.Name} in {@base.Owner} and {other.Owner} but {ancestor.Name} in {vanilla.Owner}; the bundles are laid out differently");

            // A flag such as compressed-inside-binder changes what the bytes mean, and a rebuild carries base's.
            if (own.Flags != theirs.Flags)
                throw new MergeException($"member {id} ({own.Name}) is stored differently in {@base.Owner} (0x{own.Flags:X2}) and {other.Owner} (0x{theirs.Flags:X2})");
            if (own.Flags != ancestor.Flags)
                throw new MergeException($"member {id} ({own.Name}) is stored differently in {@base.Owner} and {other.Owner} (0x{own.Flags:X2}) than in {vanilla.Owner} (0x{ancestor.Flags:X2})");
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
