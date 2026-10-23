using NexusMods.Paths;

namespace NexusMods.Games.Bethesda.Archives;

/// <summary>
/// The per-entry values a rebuilt archive has to repeat. <paramref name="Index"/> is the entry's position
/// within its folder (TES5) or the archive (TES3).
/// </summary>
public abstract record FileState(int Index, RelativePath Path);

public sealed record Tes3FileState(
    int Index,
    RelativePath Path,
    uint Offset,
    uint NameOffset,
    uint Hash1,
    uint Hash2,
    uint Size) : FileState(Index, Path);

/// <summary>
/// <paramref name="FlipCompression"/> is the entry's bit 30 flag: the entry is compressed when the archive
/// is not, and the other way round.
/// </summary>
public sealed record Tes5FileState(int Index, RelativePath Path, bool FlipCompression) : FileState(Index, Path);
