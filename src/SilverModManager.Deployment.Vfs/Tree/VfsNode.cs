namespace SilverModManager.Deployment.Vfs;

internal sealed class VfsNode
{
    public required ulong Ino { get; init; }
    public required ulong ParentIno { get; set; }

    /// <summary>The spelling of the first insertion; later case variants never change it. A rename sets it.</summary>
    public required string DisplayName { get; set; }

    public required bool IsDirectory { get; init; }

    /// <summary>Set for files only. Copy-on-write and renames replace it.</summary>
    public VfsFileSource? Source { get; set; }

    /// <summary>What the scanner saw, for a file that came from the backing directory and has not been copied into staging.</summary>
    public BackingEntry? Backing { get; set; }

    public Dictionary<VfsNameKey, VfsNode>? Children { get; init; }

    /// <summary>
    /// For a directory: it exists below staging (in the backing directory or the loadout), so it can
    /// be removed only with a whiteout and cannot be moved. A staged directory renamed over an empty
    /// lower one takes this on.
    /// </summary>
    public bool Lower { get; set; }

    /// <summary>
    /// For a staged file: something with the same name sits below it, so deleting the staged copy
    /// must also whiteout the name or the original would show through.
    /// </summary>
    public bool Shadows { get; set; }

    /// <summary>Serialises copying this file into staging, which happens outside the tree's lock.</summary>
    public object CowLock { get; } = new();
}
