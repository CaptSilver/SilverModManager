using SilverModManager.Sdk.Diagnostics;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>Reports the Skia raster bytes alive now and the peak, which the sampler's once-a-few-seconds reads would otherwise miss.</summary>
public sealed class SkiaRasterContributor : IMemoryContributor
{
    public string Name => "skia";

    public IReadOnlyList<(string Key, long Bytes)> Sample() => [("skia", SkiaRasterMemory.Live), ("skia-peak", SkiaRasterMemory.HighWater)];
}
