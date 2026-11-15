using NexusMods.Paths;

namespace SilverModManager.Wabbajack.Acquire;

/// <summary>
/// A scratch file under the app's temp root, deleted when closed. Not under <c>/tmp</c>: that is tmpfs on many
/// systems, where a multi-GB spool is memory the install then cannot use.
/// </summary>
internal static class DiskSpool
{
    /// <summary>
    /// Creates the spool, runs <paramref name="write"/> into it and rewinds it. The temp folder is made again first,
    /// since opening the file directly for DeleteOnClose, which the manager's file system cannot pass on, skips the
    /// manager's repair of a folder something removed under the running app. Disposes the spool and rethrows when
    /// <paramref name="write"/> fails.
    /// </summary>
    public static async Task<Stream> Fill(TemporaryFileManager temporaryFiles, int bufferSize, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        var path = temporaryFiles.CreateFile().Path;
        path.Parent.CreateDirectory();
        var spool = new FileStream(path.GetFullPath(), FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            bufferSize, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            await write(spool, cancellationToken);
            spool.Position = 0;
            return spool;
        }
        catch
        {
            await spool.DisposeAsync();
            throw;
        }
    }
}
