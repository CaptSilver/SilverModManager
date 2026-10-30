using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

/// <summary>Begins a job through the real job monitor, the way the app does, with a handler standing in for the host.</summary>
public abstract class HostedJobTestBase(IServiceProvider provider, TemporaryFileManager temporaryFileManager)
{
    protected TemporaryFileManager Files => temporaryFileManager;

    protected IServiceProvider Provider => provider;

    internal IJobTask<TJob, AbsolutePath> Begin<TJob>(TJob job) where TJob : IJobDefinitionWithStart<TJob, AbsolutePath>
        => provider.GetRequiredService<IJobMonitor>().Begin<TJob, AbsolutePath>(job);

    protected static byte[] Content(int length, int seed = 7)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
