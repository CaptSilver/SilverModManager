using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

public enum VfsSourceKind
{
    /// <summary>A file served from the game's own directory.</summary>
    Backing,

    /// <summary>A file served from the staging store.</summary>
    Staged,

    /// <summary>Removes an earlier node for the same path (a deleted winner over a vanilla file).</summary>
    Hidden,
}

/// <summary>
/// One file the tree serves. <paramref name="RealPath"/> is where the bytes live; it may be absent
/// for a <see cref="VfsSourceKind.Hidden"/> source, and opening a file without one fails with EIO.
/// <paramref name="FromStaging"/> marks a file the game wrote into the staging directory, as opposed to
/// one the loadout provides, so promotion can tell the two apart.
/// </summary>
public readonly record struct VfsFileSource(RelativePath Path, VfsSourceKind Kind, AbsolutePath? RealPath, Size Size, Hash Hash, bool FromStaging = false);
