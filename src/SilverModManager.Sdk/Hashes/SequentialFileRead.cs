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
    /// Opens the file for a read from the first byte to the last. On the real file system the stream has no buffer of
    /// its own: a hasher asks for <see cref="HashingLimits.ReadBlockSize"/> bytes at a time, so a 4 KB buffer would only
    /// add a lock and a copy to every read. On Linux the kernel is also told the file will be read in order, so it reads
    /// ahead further. The hint is best-effort. Any other file system (an in-memory one in tests) is read as it comes.
    /// </summary>
    public static Stream Open(AbsolutePath file)
    {
        if (file.FileSystem is not FileSystem) return file.Read();

        var stream = new FileStream(file.GetFullPath(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 0, FileOptions.SequentialScan);
        if (!stream.SafeFileHandle.IsInvalid) AdviseSequential(stream);
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
