namespace NexusMods.Deployment.Vfs;

/// <summary>
/// Hands out inode numbers: the root is 1, everything else counts up from 2. A (parent, name)
/// pair keeps its number for the life of the table, so a name that is hidden and served again is
/// the same inode to the kernel.
/// </summary>
internal sealed class InodeTable
{
    private readonly Dictionary<(ulong Parent, VfsNameKey Key), ulong> _inodes = new();
    private ulong _next = 2;

    public ulong GetOrAdd(ulong parent, VfsNameKey key)
    {
        if (_inodes.TryGetValue((parent, key), out var ino)) return ino;
        ino = _next++;
        _inodes[(parent, key)] = ino;
        return ino;
    }

    /// <summary>
    /// Re-keys an inode for a rename. Returns false if the old pair has no number.
    /// </summary>
    public bool Move(ulong parent, VfsNameKey key, ulong newParent, VfsNameKey newKey)
    {
        if (!_inodes.Remove((parent, key), out var ino)) return false;
        _inodes[(newParent, newKey)] = ino;
        return true;
    }
}
