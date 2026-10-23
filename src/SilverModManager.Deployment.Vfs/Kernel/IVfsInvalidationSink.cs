namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Where <see cref="VfsTree"/> reports that something the kernel may have cached is stale. The tree
/// folds case and the kernel's dentry cache does not, so only the tree knows which other spellings of a
/// changed name need dropping. Calls come from inside tree operations, so an implementation must queue
/// them and return; issuing the kernel notification from a request callback can deadlock.
/// </summary>
public interface IVfsInvalidationSink
{
    /// <summary>Drops the cached attributes and data of the inode.</summary>
    void InvalidateInode(ulong ino);

    /// <summary>Drops the cached lookup of exactly this spelling of a name under the parent.</summary>
    void InvalidateEntry(ulong parent, string name);
}
