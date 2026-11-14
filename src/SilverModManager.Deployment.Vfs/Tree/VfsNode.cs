namespace SilverModManager.Deployment.Vfs;

internal abstract class VfsNode
{
    public required ulong Ino { get; init; }
    public required ulong ParentIno { get; set; }

    /// <summary>The spelling of the first insertion; later case variants never change it. A rename sets it.</summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// For a directory: it exists below staging (in the backing directory or the loadout), so it can
    /// be removed only with a whiteout and cannot be moved. A staged directory renamed over an empty
    /// lower one takes this on.
    /// </summary>
    public bool Lower { get; set; }
}

internal sealed class VfsDirectory : VfsNode
{
    public Dictionary<VfsNameKey, VfsNode> Children { get; } = new();

    /// <summary>Permission bits a caller set on this directory; null serves the default.</summary>
    public uint? Mode { get; set; }

    /// <summary>Modification time a caller set on this directory; null serves the time the tree was built.</summary>
    public DateTimeOffset? MTime { get; set; }

    /// <summary>
    /// How staging spells this directory when that differs from <see cref="VfsNode.DisplayName"/> (staging
    /// holds <c>data/</c> under a game folder the tree calls <c>Data</c>). Null when they agree.
    /// </summary>
    public string? StagedName { get; set; }
}

internal sealed class VfsFile : VfsNode
{
    /// <summary>Copy-on-write and renames replace it with a modified copy; nothing clears it.</summary>
    public required VfsFileSource Source { get; set; }

    /// <summary>What the scanner saw, for a file that came from the backing directory and has not been copied into staging.</summary>
    public BackingEntry? Backing { get; set; }

    /// <summary>
    /// For a staged file: something with the same name sits below it, so deleting the staged copy
    /// must also whiteout the name or the original would show through.
    /// </summary>
    public bool Shadows { get; set; }

    /// <summary>Serialises copying this file into staging, which happens outside the tree's lock.</summary>
    public object CowLock { get; } = new();
}
