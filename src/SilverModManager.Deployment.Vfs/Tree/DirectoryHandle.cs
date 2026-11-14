using Microsoft.Win32.SafeHandles;
using NexusMods.Paths;
using SilverModManager.Sdk.Native;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// A directory held open by descriptor. When a mount is placed over the game's own folder, the folder's
/// path leads to the mount; this handle still leads to the folder underneath, so the daemon reads
/// vanilla files through it and never by path.
/// </summary>
/// <remarks>
/// Every lookup below the handle walks the relative path one segment at a time with
/// <c>O_NOFOLLOW</c>, so no worker thread ever resolves a symlink while the mount is up. A symlinked
/// directory or file inside the folder is resolved once, by the scan that runs before the mount, and
/// registered with <see cref="Attach(RelativePath, DirectoryHandle)"/> or
/// <see cref="AttachFile"/>; paths through it are then served from its own handle.
/// </remarks>
public sealed class DirectoryHandle : IDisposable
{
    private const int ENOENT = 2;
    private const int ENOTDIR = 20;

    private readonly Dictionary<string, DirectoryHandle> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DirectoryHandle Parent, string Leaf)> _files = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly bool _ownsHandle;

    private DirectoryHandle(AbsolutePath root, SafeFileHandle handle, bool ownsHandle)
    {
        Root = root;
        Handle = handle;
        _ownsHandle = ownsHandle;
    }

    /// <summary>The path the handle was opened from. Only for messages; nothing is ever reached through it.</summary>
    public AbsolutePath Root { get; }

    public SafeFileHandle Handle { get; }

    /// <summary>Throws <see cref="DirectoryNotFoundException"/> when there is no such directory, <see cref="IOException"/> for any other failure.</summary>
    public static DirectoryHandle Open(AbsolutePath root)
    {
        var errno = LibcDirectory.TryOpenDirectory(root.ToString(), out var handle);
        if (errno is ENOENT or ENOTDIR) throw new DirectoryNotFoundException($"`{root}` is not a directory.");
        if (errno != 0 || handle is null) throw new IOException($"Opening `{root}` failed with errno {errno}.");
        return new DirectoryHandle(root, handle, ownsHandle: true);
    }

    /// <summary>
    /// Opens <paramref name="relative"/> below this directory, following symlinks. Meant for the scan
    /// that runs before the mount; the result is attached to this handle by the caller.
    /// </summary>
    internal DirectoryHandle OpenFollowing(string relative)
    {
        var errno = LibcDirectory.TryOpenDirectoryFollowing(Handle, relative, out var handle);
        if (errno != 0 || handle is null) throw new IOException($"Opening `{relative}` below `{Root}` failed with errno {errno}.");
        return new DirectoryHandle(Root.Combine(RelativePath.FromUnsanitizedInput(relative)), handle, ownsHandle: true);
    }

    /// <summary>Serves everything below <paramref name="symlinkedDirectory"/> from <paramref name="target"/>. Takes ownership of it.</summary>
    public void Attach(RelativePath symlinkedDirectory, DirectoryHandle target)
    {
        lock (_gate) _directories[symlinkedDirectory.ToString()] = target;
    }

    /// <summary>Serves the file at <paramref name="symlinkedFile"/> from <paramref name="leaf"/> in <paramref name="parent"/>. Takes ownership of it.</summary>
    public void AttachFile(RelativePath symlinkedFile, DirectoryHandle parent, string leaf)
    {
        lock (_gate) _files[symlinkedFile.ToString()] = (parent, leaf);
    }

    /// <summary>Opens a regular file below the directory. Throws <see cref="FileNotFoundException"/>, <see cref="UnauthorizedAccessException"/> or <see cref="IOException"/>.</summary>
    public SafeFileHandle OpenFile(RelativePath path, FileAccess access)
    {
        var flags = access switch
        {
            FileAccess.Read => LibcDirectory.ReadOnly,
            FileAccess.Write => LibcDirectory.WriteOnly,
            _ => LibcDirectory.ReadWrite,
        };

        SafeFileHandle? file = null;
        var errno = WithParent(path, (parent, leaf) => LibcDirectory.TryOpenAt(parent, leaf, flags, out file));
        if (errno == 0 && file is not null) return file;
        throw ToException(errno == 0 ? ENOENT : errno, path);
    }

    /// <summary>The numbers of the file (following an attached symlink, never an unattached one), or null if it is not there.</summary>
    public LibcStat.Buffer? Stat(RelativePath path)
    {
        LibcStat.Buffer buffer = default;
        var errno = WithParent(path, (parent, leaf) => LibcDirectory.TryStatAt(parent, leaf, followSymlinks: false, out buffer));
        if (errno is ENOENT or ENOTDIR) return null;
        if (errno != 0) throw ToException(errno, path);
        return buffer;
    }

    // Runs action with the directory the path's last name lives in, having walked every directory on the
    // way without following a link. The longest attached prefix wins, so a symlink inside an attached
    // directory is found too. Returns the action's result, or the errno of the walk.
    private int WithParent(RelativePath path, Func<SafeFileHandle, string, int> action)
    {
        var text = path.ToString();
        var baseHandle = this;
        var remainder = text;

        lock (_gate)
        {
            if (_files.TryGetValue(text, out var alias)) return action(alias.Parent.Handle, alias.Leaf);

            var bestLength = -1;
            foreach (var (prefix, target) in _directories)
            {
                if (prefix.Length <= bestLength || prefix.Length >= text.Length) continue;
                if (text[prefix.Length] != '/' || !text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                bestLength = prefix.Length;
                baseHandle = target;
                remainder = text[(prefix.Length + 1)..];
            }
        }

        // The attached targets live as long as this handle, so using them outside the lock is safe.
        var segments = remainder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return ENOENT;

        var current = baseHandle.Handle;
        var opened = new List<SafeFileHandle>();
        try
        {
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var errno = LibcDirectory.TryOpenDirectoryAt(current, segments[i], out var next);
                if (errno != 0 || next is null) return errno == 0 ? ENOENT : errno;
                opened.Add(next);
                current = next;
            }

            return action(current, segments[^1]);
        }
        finally
        {
            foreach (var handle in opened) handle.Dispose();
        }
    }

    private static Exception ToException(int errno, RelativePath path) => errno switch
    {
        ENOENT or ENOTDIR => new FileNotFoundException($"`{path}` is not in the directory."),
        13 => new UnauthorizedAccessException($"Access to `{path}` was denied."),
        _ => new IOException($"Opening `{path}` failed with errno {errno}."),
    };

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var target in _directories.Values) target.Dispose();
            foreach (var (parent, _) in _files.Values) parent.Dispose();
            _directories.Clear();
            _files.Clear();
        }

        if (_ownsHandle) Handle.Dispose();
    }
}
