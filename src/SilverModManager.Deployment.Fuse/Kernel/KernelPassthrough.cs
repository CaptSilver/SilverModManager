using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NexusMods.Deployment.Fuse.Native;
using NexusMods.Deployment.Vfs;

namespace NexusMods.Deployment.Fuse.Kernel;

/// <summary>
/// The two libfuse calls that hand a file to the kernel for passthrough, behind an interface so a test can
/// stand in for a kernel that accepts or refuses.
/// </summary>
internal interface IPassthroughRegistrar
{
    /// <summary>Registers <paramref name="fd"/> as a backing file. Returns the backing id, or 0 with the reason in <paramref name="errno"/>.</summary>
    int TryRegister(IntPtr req, int fd, out int errno);

    void Close(IntPtr req, int backingId);
}

internal sealed class LibFusePassthroughRegistrar : IPassthroughRegistrar
{
    public int TryRegister(IntPtr req, int fd, out int errno)
    {
        // libfuse reports failure as 0, not a negative errno, so the reason can only be read from errno itself.
        var id = LibFuse3LowLevel.fuse_passthrough_open(req, fd);
        errno = id > 0 ? 0 : System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
        return id > 0 ? id : 0;
    }

    public void Close(IntPtr req, int backingId) => LibFuse3LowLevel.fuse_passthrough_close(req, backingId);
}

internal static partial class PassthroughPolicy
{
    private const int CapSysAdminBit = 21;
    private const string StatusPath = "/proc/self/status";

    [GeneratedRegex(@"^CapEff:\s*([0-9a-fA-F]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex CapEffLine();

    /// <summary>
    /// Registering a backing file with the kernel needs CAP_SYS_ADMIN, which the app does not have when run
    /// normally, so the mount only asks for passthrough when the effective set holds it.
    /// </summary>
    internal static bool HasCapSysAdmin(string statusText)
    {
        var match = CapEffLine().Match(statusText);
        if (!match.Success) return false;
        return ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask)
               && (mask & (1UL << CapSysAdminBit)) != 0;
    }

    internal static string ReadProcessStatus() => File.ReadAllText(StatusPath);
}

/// <summary>
/// Decides, per open, whether the kernel reads the file itself. A registration the kernel refuses is not
/// retried: the reason (no privilege, an unsupported filesystem) holds for every later open too.
/// </summary>
/// <remarks>
/// Only read-only opens are offered. Once the kernel owns the descriptor, writes stop reaching the tree, and
/// the tree has to see them to copy a backing file before it changes and to track what the game staged.
/// </remarks>
internal sealed class KernelPassthrough(IPassthroughRegistrar registrar, ILogger logger)
{
    private readonly ConcurrentDictionary<ulong, int> _backingIds = new();
    private volatile bool _enabled;
    private int _refused;

    /// <summary>Called from init once the kernel agreed to passthrough.</summary>
    internal void Enable() => _enabled = true;

    /// <summary>
    /// Registers the file's descriptor and sets the backing id on <paramref name="info"/>. False means the open
    /// is answered the ordinary way and reads reach the daemon.
    /// </summary>
    internal bool TryAttach(IntPtr req, ulong handle, VfsOpenFile file, ref FuseFileInfo info)
    {
        if (!_enabled || Volatile.Read(ref _refused) != 0 || file.Writable || file.DirectIo) return false;

        int id, errno;
        var held = false;
        try
        {
            file.Handle.DangerousAddRef(ref held);
            id = registrar.TryRegister(req, (int)file.Handle.DangerousGetHandle(), out errno);
        }
        finally
        {
            if (held) file.Handle.DangerousRelease();
        }

        if (id > 0)
        {
            info.BackingId = id;
            _backingIds[handle] = id;
            return true;
        }

        if (Interlocked.Exchange(ref _refused, 1) == 0)
            logger.LogInformation("kernel passthrough refused: {Errno}; reads go through the daemon", NameOf(errno));
        return false;
    }

    /// <summary>
    /// Closes the handle's backing id so the kernel does not keep the file. Without a request there is nothing
    /// to close it with (the session is being torn down, which drops them all), so the id is only forgotten.
    /// </summary>
    internal void Release(IntPtr req, ulong handle)
    {
        if (!_backingIds.TryRemove(handle, out var id) || req == IntPtr.Zero) return;
        registrar.Close(req, id);
    }

    private static string NameOf(int errno) =>
        Enum.IsDefined(typeof(Errno), errno) ? ((Errno)errno).ToString() : $"errno {errno}";
}
