using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Sdk.Diagnostics;

namespace SilverModManager.Sdk.Tests.Diagnostics;

public class SmapsDumpTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private const string Smaps = """
        55d0-55d8 r-xp 00000000 08:01 12 /usr/bin/dotnet
        Size:                 32 kB
        Anonymous:             0 kB
        55e0-5600 rw-p 00000000 00:00 0 [heap]
        Size:             900000 kB
        Anonymous:        800000 kB
        7f10-7f90 rw-p 00000000 00:00 0
        Size:             500000 kB
        Anonymous:        400000 kB
        7fa0-7fb0 rw-p 00000000 00:00 0 /memfd:doublemapper (deleted)
        Size:               1000 kB
        Anonymous:          1000 kB
        """;

    private static MemorySample Sample(long anon, long committed) => new(0, committed, 0, anon, null);

    private static SmapsDump Create(TimeProvider time, int threshold = 4, TimeSpan? gap = null)
        => new(time, threshold * Gb, gap, () => "Rss: 1 kB", () => Smaps);

    [Fact]
    public void LargestAnonymousPicksTheBiggestAnonymousMappingsWithTheirLabels()
    {
        var largest = SmapsDump.LargestAnonymous(Smaps, 3);

        largest.Should().Equal(
            ("55e0-5600", "[heap]", 800000L * 1024),
            ("7f10-7f90", "[anon]", 400000L * 1024),
            ("7fa0-7fb0", "/memfd:doublemapper (deleted)", 1000L * 1024));
    }

    [Fact]
    public void LargestAnonymousKeepsAtMostTheCountAsked()
    {
        SmapsDump.LargestAnonymous(Smaps, 1).Should().ContainSingle();
    }

    [Fact]
    public void NothingBelowTheThreshold()
    {
        Create(new FakeTimeProvider()).TryDump(Sample(10 * Gb, 7 * Gb)).Should().BeNull();
    }

    [Fact]
    public void NothingWhenTheKernelGaveNoAnonymousCount()
    {
        Create(new FakeTimeProvider()).TryDump(new MemorySample(0, 0, 0, null, null)).Should().BeNull();
    }

    [Fact]
    public void ADumpNamesTheLargestMappings()
    {
        var text = Create(new FakeTimeProvider()).TryDump(Sample(20 * Gb, 5 * Gb));

        text.Should().Contain("[heap]").And.Contain("Rss: 1 kB");
    }

    [Fact]
    public void ADumpIsNotRepeatedWithinTheGap()
    {
        var time = new FakeTimeProvider();
        var dump = Create(time);
        var big = Sample(20 * Gb, 5 * Gb);

        dump.TryDump(big).Should().NotBeNull();
        time.Advance(TimeSpan.FromMinutes(4));
        dump.TryDump(big).Should().BeNull();
        time.Advance(TimeSpan.FromMinutes(2));
        dump.TryDump(big).Should().NotBeNull();
    }

    [Fact]
    public void TheLineSaysHowLongTheReadTook()
    {
        var time = new FakeTimeProvider();
        var dump = new SmapsDump(time, Gb, null, () => "r", () =>
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            return Smaps;
        });

        dump.TryDump(Sample(20 * Gb, 5 * Gb)).Should().EndWith("; read took 250 ms");
    }
}
