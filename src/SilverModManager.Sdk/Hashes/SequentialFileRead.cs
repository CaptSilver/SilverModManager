using System.Runtime.InteropServices;
using NexusMods.Paths;

namespace SilverModManager.Sdk.Hashes;

/// <summary>
/// Opens a file for a front-to-back read, such as hashing it whole.
/// </summary>
public static class SequentialFileRead
{
    private const int PosixFadvSequential = 2;

    /// <summary>
    /// Opens the file through its file system and, on Linux, tells the kernel it will be read in order so it reads
    /// ahead further. The stream's own 4 KB buffer is bypassed by any read at least that large, so a hasher asking
    /// for <see cref="HashingLimits.ReadBlockSize"/> bytes at a time reaches the disk directly. The hint is
    /// best-effort: a file system that does not return a <see cref="FileStream"/> is read as it comes.
    /// </summary>
    public static Stream Open(AbsolutePath file)
    {
        var stream = file.Read();
        if (stream is FileStream { SafeFileHandle.IsInvalid: false } fileStream) AdviseSequential(fileStream);
        return stream;
    }

    private static void AdviseSequential(FileStream stream)
    {
        var handle = stream.SafeFileHandle;
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            _ = posix_fadvise((int)handle.DangerousGetHandle(), 0, 0, PosixFadvSequential);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // A hint only: the read is correct without it.
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int posix_fadvise(int fd, long offset, long length, int advice);
}
