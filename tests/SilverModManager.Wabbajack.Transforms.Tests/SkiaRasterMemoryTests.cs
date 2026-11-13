using BCnEncoder.Shared;
using FluentAssertions;
using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

/// <summary>The counter is process-wide, so these tests share a collection and never overlap with each other or with a resize elsewhere.</summary>
[Collection(Collection)]
public class SkiaRasterMemoryTests
{
    public const string Collection = "skia raster memory";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResizeRaisesLiveWhileRunningAndReturnsIt(bool srgb)
    {
        var before = SkiaRasterMemory.Live;
        SkiaRasterMemory.ResetHighWater();
        var pixels = new ColorRgba32[64 * 64];
        Array.Fill(pixels, new ColorRgba32(10, 20, 30, 255));

        DdsRecompressor.Resize(pixels, 64, 64, 32, 32, srgb);

        SkiaRasterMemory.Live.Should().Be(before);
        // The source bitmap and the resized one are alive together; sRGB works on four-byte floats per channel.
        var bytesPerPixel = srgb ? 16 : 4;
        SkiaRasterMemory.HighWater.Should().BeGreaterThanOrEqualTo(before + (64 * 64 + 32 * 32) * bytesPerPixel);
    }

    [Fact]
    public void RemoveGivesBackWhatAddTook()
    {
        var before = SkiaRasterMemory.Live;

        SkiaRasterMemory.Add(1000);
        SkiaRasterMemory.Live.Should().Be(before + 1000);
        SkiaRasterMemory.Remove(1000);

        SkiaRasterMemory.Live.Should().Be(before);
    }

    [Fact]
    public void TheContributorReportsLiveAndPeak()
    {
        SkiaRasterMemory.Add(4096);
        try
        {
            new SkiaRasterContributor().Sample().Should().Contain([("skia", SkiaRasterMemory.Live), ("skia-peak", SkiaRasterMemory.HighWater)]);
        }
        finally
        {
            SkiaRasterMemory.Remove(4096);
        }
    }
}
