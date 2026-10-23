// Follows Wabbajack.Compression.BSA/BA2Archive/DX10Entry.cs (GPL-3.0).
using NexusMods.Paths;

namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// A texture. The archive stores only the mip data, so reading it yields a DDS file: a header built from
/// the entry's dimensions and format, then every chunk in order.
/// </summary>
internal sealed class Ba2TextureEntry : IBethesdaArchiveEntry
{
    /// <summary>Where a chunk's bytes sit in the archive; its other values are in the state.</summary>
    public readonly record struct ChunkLocation(long Offset, uint PackedSize);

    private readonly ArchiveSource _source;
    private readonly Ba2Dx10FileState _state;
    private readonly ChunkLocation[] _locations;
    private readonly byte[] _header;
    private readonly bool _lz4;

    public Ba2TextureEntry(ArchiveSource source, Ba2Dx10FileState state, ChunkLocation[] locations, bool lz4)
    {
        _source = source;
        _state = state;
        _locations = locations;
        _lz4 = lz4;
        _header = DdsHeader.Build(state.Width, state.Height, state.NumMips, state.PixelFormat, state.IsCubeMap == 1);
        Size = (ulong)_header.Length + (ulong)state.Chunks.Sum(c => (long)c.FullSz);
    }

    public RelativePath Path => _state.Path;

    public ulong Size { get; }

    public FileState State => _state;

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken)
    {
        using var _ = await _source.Lock(cancellationToken);
        await destination.WriteAsync(_header, cancellationToken);
        for (var i = 0; i < _state.Chunks.Length; i++)
        {
            await Ba2Payload.CopyTo(
                _source, _locations[i].Offset, _locations[i].PackedSize, _state.Chunks[i].FullSz, _lz4,
                destination, Path.ToString(), cancellationToken);
        }
    }
}
