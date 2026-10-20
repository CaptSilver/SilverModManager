namespace NexusMods.Deployment.Vfs;

internal sealed class VfsNode
{
    public required ulong Ino { get; init; }
    public required ulong ParentIno { get; init; }

    /// <summary>The spelling of the first insertion; later case variants never change it.</summary>
    public required string DisplayName { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>Set for files only.</summary>
    public VfsFileSource? Source { get; init; }

    /// <summary>What the scanner saw, for a file that came from the backing directory.</summary>
    public BackingEntry? Backing { get; init; }

    public Dictionary<VfsNameKey, VfsNode>? Children { get; init; }
}
