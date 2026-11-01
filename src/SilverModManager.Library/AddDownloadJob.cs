using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Library.Jobs;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Library;

internal class AddDownloadJob : IJobDefinitionWithStart<AddDownloadJob, LibraryFile.ReadOnly>, IAddDownloadJob
{ 
    public required IJobTask<IDownloadJob, AbsolutePath> DownloadJob { get; init; }
    public required IAcquisitionContext Acquisition { get; init; }
    internal required IConnection Connection { get; set; }
    internal required IServiceProvider ServiceProvider { get; set; }
    
    public static IJobTask<AddDownloadJob, LibraryFile.ReadOnly> Create(IServiceProvider provider, IJobTask<IDownloadJob, AbsolutePath> downloadJob, IAcquisitionContext acquisition)
    {
        var monitor = provider.GetRequiredService<IJobMonitor>();
        var job = new AddDownloadJob
        {
            DownloadJob = downloadJob,
            Acquisition = acquisition,
            Connection = provider.GetRequiredService<IConnection>(),
            ServiceProvider = provider,
        };
        return monitor.Begin<AddDownloadJob, LibraryFile.ReadOnly>(job);
    }

    public async ValueTask<LibraryFile.ReadOnly> StartAsync(IJobContext<AddDownloadJob> context)
    {
        await context.YieldAsync();

        // This throws if download cancelled, will be caught downstream.
        await DownloadJob;

        await context.YieldAsync();
        using var tx = Connection.BeginTransaction();

        var libraryFile = await AddLibraryFileJob.Create(ServiceProvider, tx, DownloadJob.Result, Acquisition);
        await DownloadJob.JobDefinition.AddMetadata(tx, libraryFile);

        var transactionResult = await tx.Commit();
        return transactionResult.Remap(libraryFile);
    }
}
