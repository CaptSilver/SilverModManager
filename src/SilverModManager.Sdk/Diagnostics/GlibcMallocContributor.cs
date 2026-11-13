using System.Runtime.InteropServices;

namespace SilverModManager.Sdk.Diagnostics;

/// <summary>
/// The C allocator's own counts, from <c>mallinfo2</c>. glibc's <c>__libc_mallinfo2</c> (malloc/malloc.c) walks every arena,
/// so the totals cover the whole process; the man page's BUGS note about the main arena only is out of date for glibc 2.33
/// and later. <c>malloc-used</c> is what native code has allocated and not freed, <c>malloc-free</c> what the allocator holds
/// but has not returned to the OS.
/// </summary>
public sealed class GlibcMallocContributor : IMemoryContributor
{
    public string Name => "glibc-malloc";

    public IReadOnlyList<(string Key, long Bytes)> Sample()
    {
        try
        {
            var info = mallinfo2();
            return FromStruct((long)info.arena, (long)info.hblkhd, (long)info.uordblks, (long)info.fordblks);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // musl or a glibc older than 2.33: no counts rather than a failed install log line.
            return [];
        }
    }

    internal static IReadOnlyList<(string, long)> FromStruct(long arena, long hblkhd, long uordblks, long fordblks)
        => [("malloc-arena", arena), ("malloc-mmap", hblkhd), ("malloc-used", uordblks), ("malloc-free", fordblks)];

    [StructLayout(LayoutKind.Sequential)]
    private struct MallInfo2
    {
        public nuint arena;
        public nuint ordblks;
        public nuint smblks;
        public nuint hblks;
        public nuint hblkhd;
        public nuint usmblks;
        public nuint fsmblks;
        public nuint uordblks;
        public nuint fordblks;
        public nuint keepcost;
    }

    [DllImport("libc.so.6")]
    private static extern MallInfo2 mallinfo2();
}
