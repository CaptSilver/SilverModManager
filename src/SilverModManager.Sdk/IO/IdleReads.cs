using SilverModManager.Sdk.Errors;

namespace SilverModManager.Sdk.IO;

/// <summary>
/// Reads from a network body with a limit on how long it may stay silent. A connection that dies without a reset
/// (a dropped VPN hop, a proxy whose upstream went away) stays open forever, and neither HttpClient's timeout nor
/// cancellation by the user covers a body that is already streaming.
/// </summary>
public static class IdleReads
{
    /// <summary>How long a body may send nothing before the connection is treated as dead.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One read that gives up after <paramref name="idleTimeout"/> without a byte. The user's cancellation is
    /// rethrown as it came; only silence becomes a <see cref="DownloadStalledException"/>.
    /// </summary>
    public static async ValueTask<int> ReadAsync(Stream source, Memory<byte> buffer, string host, TimeSpan idleTimeout, TimeProvider time, CancellationToken token)
    {
        using var idle = new CancellationTokenSource();
        // TimeProvider has no public cancel-after, so a timer on it trips the source.
        using var timer = time.CreateTimer(static state => (state as CancellationTokenSource)?.Cancel(), idle, idleTimeout, Timeout.InfiniteTimeSpan);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, idle.Token);
        try
        {
            return await source.ReadAsync(buffer, linked.Token);
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new DownloadStalledException(host);
        }
    }

    /// <summary>The whole body in memory, for the small pieces a host serves in parts.</summary>
    public static async Task<byte[]> ReadToEndAsync(Stream source, string host, TimeSpan idleTimeout, TimeProvider time, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await ReadAsync(source, buffer, host, idleTimeout, time, token)) > 0)
            result.Write(buffer, 0, read);
        return result.ToArray();
    }
}
