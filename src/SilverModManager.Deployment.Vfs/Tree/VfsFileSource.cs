using SilverModManager.Sdk.Hashes;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;

namespace SilverModManager.Deployment.Vfs;

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
/// A <see cref="VfsSourceKind.Backing"/> file made by <see cref="FromBacking"/> has no path at all:
/// <paramref name="Backing"/> is the directory it is read from, because the mount may cover the
/// directory's own path.
/// <paramref name="FromStaging"/> marks a file the game wrote into the staging directory, as opposed to
/// one the loadout provides, so promotion can tell the two apart.
/// </summary>
public readonly record struct VfsFileSource(
    RelativePath Path,
    VfsSourceKind Kind,
    AbsolutePath? RealPath,
    Size Size,
    FileIdentity Identity,
    bool FromStaging = false,
    DirectoryHandle? Backing = null)
{
    /// <summary>A file of <paramref name="backing"/> as the scanner saw it, read only through the handle.</summary>
    public static VfsFileSource FromBacking(DirectoryHandle backing, BackingEntry entry) =>
        new(entry.Path, VfsSourceKind.Backing, RealPath: null, entry.Size, FileIdentity.None, FromStaging: false, backing);
}
