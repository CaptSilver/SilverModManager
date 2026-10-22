using NexusMods.Games.FromSoft.Formats;

namespace NexusMods.Games.FromSoft.Merge;

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
                    notes.Add(new TextureKept(texture.Name, baseName ?? "the preferred mod", otherName ?? "the other mod"));
            }
        }

        var extra = otherArchive.Textures.Where(t => !have.ContainsKey(t.Name)).ToArray();
        if (extra.Length == 0) return @base.ToArray();

        return Dcx.Write(Tpf.Write(baseArchive.WithTextures([.. baseArchive.Textures, .. extra])), traits.PackedWrite);
    }
}
