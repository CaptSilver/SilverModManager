using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace SilverModManager.Benchmarks.Benchmarks.Textures;

/// <summary>
/// One warm-up and three measured iterations, in this process. A managed BC7 level takes tens of seconds, so the
/// BenchmarkDotNet defaults would run these for hours; three medians are enough to tell 3x from 30x.
/// Run it under <c>taskset -c 0-3 nice -n 19</c>: the machine also belongs to someone else.
/// </summary>
public sealed class TextureBenchmarkConfig : ManualConfig
{
    // The default limit is five minutes for a whole benchmark, which a managed BC7 level at 2048 pixels overruns.
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(2);

    public TextureBenchmarkConfig()
    {
        AddJob(Job.Default.WithToolchain(new InProcessEmitToolchain(Timeout, logOutput: false)).WithWarmupCount(1).WithIterationCount(3));
        AddColumn(new MpixPerSecondColumn(), new PsnrColumn());
        HideColumns("Error", "StdDev", "Job", "Mean");
        WithOptions(ConfigOptions.DisableOptimizationsValidator);
    }
}
