namespace NexusMods.Games.Bethesda.Archives;

internal static class ArchiveCopy
{
    /// <summary>
    /// Copies exactly <paramref name="count"/> bytes. The reads are synchronous: the source is an
    /// in-process seekable stream (or a decompressor over one), and the lock around it is already async.
    /// </summary>
    public static async Task CopyExactly(
        Stream source,
        Stream destination,
        ulong count,
        ArchiveKind kind,
        string what,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[(int)Math.Min(count, 128 * 1024UL)];
        var remaining = count;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = source.Read(buffer, 0, (int)Math.Min((ulong)buffer.Length, remaining));
            if (read == 0)
                throw new BethesdaArchiveException($"{kind} archive entry {what} ended {remaining} byte(s) early");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= (ulong)read;
        }
    }
}
