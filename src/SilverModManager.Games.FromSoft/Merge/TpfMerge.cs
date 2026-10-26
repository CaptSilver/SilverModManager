using SilverModManager.Games.FromSoft.Formats;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Merges two menu texture archives (.tpf.dcx) that both claim the same file. Two mods that both ship one are usually
/// not in conflict: each carries the whole vanilla atlas set with its own icons layered in.
/// </summary>
public static class TpfMerge
{
    /// <summary>
    /// Adds the other archive's new textures to base's. Two-way rather than three-way: the vanilla archive lives inside
    /// the game's own archives, and a mod adding icons ships them under names of its own, so a new name identifies the
    /// contribution without needing to know what vanilla held. Where both sides ship a name, base wins: the shared
    /// names are overwhelmingly identical copies, and where they differ the preferred mod's is the one with its own
    /// icons in it.
    /// </summary>
    /// <param name="notes">Receives a <see cref="TextureKept"/> for every shared name whose bytes differ; null collects nothing.</param>
    /// <param name="baseName">Who owns base, for the notes; a generic name when null.</param>
    /// <param name="otherName">Who owns the other archive, for the notes; a generic name when null.</param>
    /// <returns>The base bytes untouched when the other side adds nothing, so an unchanged file is not recompressed.</returns>
    public static byte[] Union(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, FromSoftFormatTraits traits,
        ICollection<IMergeNote>? notes = null, string? baseName = null, string? otherName = null)
    {
        var baseArchive = Tpf.Read(Dcx.Read(@base, traits));
        var otherArchive = Tpf.Read(Dcx.Read(other, traits));

        // First copy per name: an archive can carry a name twice, and a duplicate must not abort the merge.
        var have = new Dictionary<string, Texture>();
        foreach (var texture in baseArchive.Textures) have.TryAdd(texture.Name, texture);
        if (notes is not null)
        {
            foreach (var texture in otherArchive.Textures)
            {
                if (have.TryGetValue(texture.Name, out var own) && !own.Data.AsSpan().SequenceEqual(texture.Data))
                    notes.Add(new TextureKept(texture.Name, baseName ?? MergeNames.PreferredMod, otherName ?? MergeNames.OtherMod));
            }
        }

        var extra = otherArchive.Textures.Where(t => !have.ContainsKey(t.Name)).ToArray();
        if (extra.Length == 0) return @base.ToArray();

        return Dcx.Write(Tpf.Write(baseArchive.WithTextures([.. baseArchive.Textures, .. extra])), traits.PackedWrite);
    }

    /// <summary>
    /// Three-way merge of two raw (not DCX-wrapped) TPFs by texture name, with absence as a value: a texture only the
    /// other side changed is taken, one only the other side added is added, one only the other side removed is removed.
    /// A texture both sides moved differently stays as base has it, deletion included, and is noted. Equality covers the
    /// data and every entry field, so a metadata-only change counts as a change.
    /// </summary>
    /// <param name="notes">Receives a note for every texture both sides moved; null collects nothing.</param>
    /// <param name="baseName">Who owns base, for the notes; a generic name when null.</param>
    /// <param name="otherName">Who owns the other archive, for the notes; a generic name when null.</param>
    /// <param name="keptName">Who the conflict notes credit with the kept copy when that is not base's owner; defaults to <paramref name="baseName"/>.</param>
    /// <returns>The base bytes untouched when nothing is taken. Output order is base's textures, then additions in the other's order.</returns>
    /// <exception cref="TpfException">An input does not parse, repeats a texture name, or the result cannot be written.</exception>
    public static byte[] ThreeWay(
        ReadOnlySpan<byte> @base, ReadOnlySpan<byte> other, ReadOnlySpan<byte> vanilla,
        ICollection<IMergeNote>? notes = null, string? baseName = null, string? otherName = null, string? keptName = null)
    {
        var kept = keptName ?? baseName ?? MergeNames.PreferredMod;
        var dropped = otherName ?? MergeNames.OtherMod;
        var baseArchive = Tpf.Read(@base);
        var baseTextures = ByName(baseArchive, "base");
        var otherArchive = Tpf.Read(other);
        var otherTextures = ByName(otherArchive, "other");
        var vanillaTextures = ByName(Tpf.Read(vanilla), "vanilla");

        var result = new List<Texture>();
        var taken = false;

        foreach (var own in baseArchive.Textures)
        {
            otherTextures.TryGetValue(own.Name, out var o);
            vanillaTextures.TryGetValue(own.Name, out var v);
            if (Same(o, own) || Same(o, v)) { result.Add(own); continue; }   // nobody disagrees, or only base moved
            if (Same(own, v))
            {
                taken = true;                                                 // only the other side moved
                if (o is not null) result.Add(o);
                continue;
            }
            if (o is null) notes?.Add(new TextureDeletionConflict(own.Name, kept, dropped, KeptDeleted: false));
            else notes?.Add(new TextureKept(own.Name, kept, dropped));
            result.Add(own);
        }

        foreach (var added in otherArchive.Textures.Where(t => !baseTextures.ContainsKey(t.Name)))
        {
            vanillaTextures.TryGetValue(added.Name, out var v);
            if (Same(added, v)) continue;                                     // base deleted it, the other left it alone
            if (v is null)
            {
                taken = true;
                result.Add(added);
            }
            else
            {
                notes?.Add(new TextureDeletionConflict(added.Name, kept, dropped, KeptDeleted: true));
            }
        }

        return taken ? Tpf.Write(baseArchive.WithTextures(result)) : @base.ToArray();
    }

    private static Dictionary<string, Texture> ByName(TpfArchive archive, string side)
    {
        var byName = new Dictionary<string, Texture>();
        foreach (var texture in archive.Textures)
        {
            if (!byName.TryAdd(texture.Name, texture))
                throw new TpfException($"the {side} TPF holds the texture name '{texture.Name}' more than once, so a merge by name cannot tell its copies apart");
        }
        return byName;
    }

    private static bool Same(Texture? a, Texture? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Format == b.Format && a.Type == b.Type && a.Mipmaps == b.Mipmaps && a.Flags1 == b.Flags1
              && a.Data.AsSpan().SequenceEqual(b.Data);
}
