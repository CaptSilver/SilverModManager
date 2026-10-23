using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

/// <summary>
/// <see cref="IThunderstoreSource"/> over <see cref="AddThunderstorePackageJob"/>.
/// </summary>
internal sealed class ThunderstoreSource(IServiceProvider provider) : IThunderstoreSource
{
    public async ValueTask<IReadOnlyList<LibraryFile.ReadOnly>> AddAsync(ThunderstoreInstallRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var job = AddThunderstorePackageJob.Create(provider, request);

        // The job runs on the monitor's own token, so the caller's is forwarded as a cancel of the job.
        var monitor = provider.GetRequiredService<IJobMonitor>();
        await using var registration = cancellationToken.Register(() => monitor.Cancel(job));
        return await job;
    }
}
