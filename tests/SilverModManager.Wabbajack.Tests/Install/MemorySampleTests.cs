using FluentAssertions;
using SilverModManager.Wabbajack.Install;

namespace SilverModManager.Wabbajack.Tests.Install;

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
        new ProcessMemorySampler().Sample().CommittedHeapBytes.Should().BeGreaterThan(0);
    }
}
