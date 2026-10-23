using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// The content type a BA2 holds, from the four character code in its header.
/// </summary>
public enum Ba2EntryType
{
    /// <summary>General files.</summary>
    Gnrl,

    /// <summary>Textures stored as chunks of mip levels.</summary>
    Dx10,

    /// <summary>PlayStation 4 textures.</summary>
    Gnmf,
}

/// <summary>
/// Fallout 4 and later. <paramref name="Version"/> is 1, 7 or 8 for Fallout 4, 2 or 3 for Starfield.
/// <paramref name="Unknown1"/>, <paramref name="Unknown2"/> and <paramref name="Compression"/> exist from
/// version 2 on (compression from version 3) and are zero before. A <paramref name="Compression"/> of 3
/// means LZ4 blocks where the other value means zlib.
/// </summary>
public sealed record Ba2ArchiveState(
    bool HasNameTable,
    Ba2EntryType Type,
    string HeaderMagic,
    uint Version,
    uint Unknown1,
    uint Unknown2,
    uint Compression) : ArchiveState;

/// <summary>
/// A general file entry. The hashes and <paramref name="Extension"/> are the header values a rebuilt archive
/// repeats; <paramref name="Compressed"/> is whether the bytes on disk are compressed.
/// </summary>
public sealed record Ba2FileState(
    int Index,
    RelativePath Path,
    string Extension,
    bool Compressed,
    uint Align,
    uint Flags,
    uint DirHash,
    uint NameHash) : FileState(Index, Path);

/// <summary>
/// One run of mip levels of a texture, stored (and compressed) as a unit.
/// </summary>
public sealed record Ba2Chunk(bool Compressed, uint Align, ushort EndMip, ushort StartMip, uint FullSz);

/// <summary>
/// A texture entry. <paramref name="PixelFormat"/> is a DXGI format number.
/// </summary>
public sealed record Ba2Dx10FileState(
    int Index,
    RelativePath Path,
    Ba2Chunk[] Chunks,
    byte PixelFormat,
    byte NumMips,
    ushort Width,
    ushort Height,
    ushort ChunkHdrLen,
    byte Unk8,
    uint DirHash,
    string Extension,
    uint NameHash,
    byte IsCubeMap,
    byte TileMode) : FileState(Index, Path)
{
    // Chunks is an array, so the generated equality would compare it by reference.
    public bool Equals(Ba2Dx10FileState? other) =>
        other is not null
        && Index == other.Index && Path == other.Path && PixelFormat == other.PixelFormat
        && NumMips == other.NumMips && Width == other.Width && Height == other.Height
        && ChunkHdrLen == other.ChunkHdrLen && Unk8 == other.Unk8 && DirHash == other.DirHash
        && Extension == other.Extension && NameHash == other.NameHash && IsCubeMap == other.IsCubeMap
        && TileMode == other.TileMode && Chunks.SequenceEqual(other.Chunks);

    public override int GetHashCode() => HashCode.Combine(Index, Path, PixelFormat, Width, Height, NameHash, Chunks.Length);
}
