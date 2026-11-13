namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// The bytes of Skia raster bitmaps alive right now and the most there have been at once. Skia allocates them with plain
/// <c>malloc</c>, so they are part of the C allocator's used bytes and nothing else counts them.
/// </summary>
public static class SkiaRasterMemory
{
    private static long s_live;
    private static long s_highWater;

    public static long Live => Interlocked.Read(ref s_live);

    public static long HighWater => Interlocked.Read(ref s_highWater);

    public static void Add(long bytes)
    {
        var live = Interlocked.Add(ref s_live, bytes);
        long seen;
        while (live > (seen = Interlocked.Read(ref s_highWater)) && Interlocked.CompareExchange(ref s_highWater, live, seen) != seen)
        {
        }
    }

    public static void Remove(long bytes) => Interlocked.Add(ref s_live, -bytes);

    /// <summary>Starts the peak over from what is alive now, so a test can read the peak of one operation.</summary>
    internal static void ResetHighWater() => Interlocked.Exchange(ref s_highWater, Live);

    /// <summary>Counts a bitmap's pixels for as long as the lease lives: <c>using var lease = new RasterLease(bitmap.ByteCount)</c>.</summary>
    internal readonly struct RasterLease : IDisposable
    {
        private readonly long _bytes;

        public RasterLease(long bytes)
        {
            _bytes = bytes;
            Add(bytes);
        }

        public void Dispose() => Remove(_bytes);
    }
}
