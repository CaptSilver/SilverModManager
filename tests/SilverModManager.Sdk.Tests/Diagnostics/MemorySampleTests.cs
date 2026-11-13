using FluentAssertions;
using SilverModManager.Sdk.Diagnostics;

namespace SilverModManager.Sdk.Tests.Diagnostics;

public class MemorySampleTests
{
    private const string Status = "Name:\tdotnet\nVmRSS:\t 31457280 kB\nRssAnon:\t 31560000 kB\nRssFile:\t  100 kB\nRssShmem:\t   102400 kB\nThreads:\t40\n";

    [Fact]
    public void ParseStatusReadsAnonAndShmemInBytes()
    {
        var (anon, shmem) = ProcessMemorySampler.ParseStatus(Status);

        anon.Should().Be(31_560_000L * 1024);
        shmem.Should().Be(102_400L * 1024);
    }

    [Fact]
    public void ParseStatusGivesNullsForATextWithoutThoseLines()
    {
        var (anon, shmem) = ProcessMemorySampler.ParseStatus("Name:\tdotnet\nVmRSS:\t 100 kB\n");

        anon.Should().BeNull();
        shmem.Should().BeNull();
    }

    [Fact]
    public void DescribeListsEveryCountInGigabytes()
    {
        var sample = new MemorySample(13_204_000_000, 21_474_836_480, 45, 32_300_000_000, 107_374_182);

        sample.Describe().Should().Be("heap 12.3 GB (committed 20.0 GB), gen2 45, anon 30.1 GB, shmem 0.1 GB");
    }

    [Fact]
    public void DescribeLeavesOutCountsTheOsDidNotGive()
    {
        new MemorySample(1_073_741_824, 2_147_483_648, 2, null, null).Describe().Should().Be("heap 1.0 GB (committed 2.0 GB), gen2 2");
    }

    [Fact]
    public void TheProcessSamplerReportsALiveHeap()
    {
        new ProcessMemorySampler().Sample().ManagedHeapBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void TheProcessSamplerReportsTheHeapTheGcHasCommitted()
    {
        // The GC reports committed bytes from its last collection, so a fresh test host reads zero until one has run.
        GC.Collect();
        new ProcessMemorySampler().Sample().CommittedHeapBytes.Should().BeGreaterThan(0);
    }

    private const long Gb = 1024L * 1024 * 1024;

    // The pinned block-cache bytes are already part of the block cache's usage, so they print outside the parentheses and
    // do not come off "other" a second time.
    [Fact]
    public void DescribeNativeShowsRocksDbAndSkiaInsideMallocUsed()
    {
        var sample = new MemorySample(0, 0, 0, null, null,
            Native:
            [
                ("malloc-arena", 20 * Gb), ("malloc-mmap", Gb), ("malloc-used", (long)(4.9 * Gb)), ("malloc-free", (long)(9.8 * Gb)),
                ("rocksdb-readers", (long)(3.1 * Gb)), ("rocksdb-memtables", (long)(0.1 * Gb)), ("rocksdb-cache", (long)(0.4 * Gb)),
                ("rocksdb-cache-pinned", (long)(0.3 * Gb)), ("rocksdb-cache-capacity", 8 * Gb),
                ("skia", (long)(0.5 * Gb)), ("skia-peak", 2 * Gb),
            ],
            NativeNotes: ["rocksdb: librocksdb.so"]);

        sample.DescribeNative().Should().Be(
            "malloc-used 4.9 GB (rocksdb-readers 3.1 GB, rocksdb-memtables 0.1 GB, rocksdb-cache 0.4 GB, skia 0.5 GB, other 0.8 GB), malloc-free 9.8 GB, "
            + "malloc-arena 20.0 GB, malloc-mmap 1.0 GB, rocksdb-cache-pinned 0.3 GB, rocksdb-cache-capacity 8.0 GB, skia-peak 2.0 GB, rocksdb: librocksdb.so");
    }

    [Fact]
    public void DescribeNativeClampsTheOtherShareAtZero()
    {
        var sample = new MemorySample(0, 0, 0, null, null, Native: [("malloc-used", Gb), ("rocksdb-readers", 3 * Gb)]);

        sample.DescribeNative().Should().Be("malloc-used 1.0 GB (rocksdb-readers 3.0 GB, other 0.0 GB)");
    }

    [Fact]
    public void DescribeNativeIsEmptyWithoutContributors()
    {
        new MemorySample(0, 0, 0, null, null).DescribeNative().Should().BeEmpty();
        new MemorySample(0, 0, 0, null, null, Native: []).DescribeNative().Should().BeEmpty();
    }

    [Fact]
    public void GlibcMallocFromStructNamesTheFourCounts()
    {
        GlibcMallocContributor.FromStruct(10, 20, 30, 40).Should().Equal(("malloc-arena", 10L), ("malloc-mmap", 20L), ("malloc-used", 30L), ("malloc-free", 40L));
    }

    [Fact]
    public void TheProcessSamplerWithTheGlibcContributorReportsMallocUsedOnThisHost()
    {
        var sample = new ProcessMemorySampler([new GlibcMallocContributor()]).Sample();

        sample.Native.Should().NotBeNull();
        sample.Native!.Single(n => n.Key == "malloc-used").Bytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AContributorsNoteReachesTheSample()
    {
        var sample = new ProcessMemorySampler([new FixedContributor()]).Sample();

        sample.Native.Should().Equal(("rocksdb-readers", 5L));
        sample.NativeNotes.Should().Equal("rocksdb: librocksdb.so");
    }

    private sealed class FixedContributor : IMemoryContributor
    {
        public string Name => "fixed";
        public IReadOnlyList<(string Key, long Bytes)> Sample() => [("rocksdb-readers", 5)];
        public string? Note => "rocksdb: librocksdb.so";
    }
}
