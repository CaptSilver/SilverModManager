using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace SilverModManager.Benchmarks;

/// <summary>
/// A short run in this process. The generated per-benchmark project takes longer to build than BenchmarkDotNet
/// waits for when it has to restore this solution's dependency graph.
/// </summary>
public sealed class InProcessShortRunConfig : ManualConfig
{
    public InProcessShortRunConfig() => AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance));
}
