using System.Reactive.Disposables;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Downloads;
using NexusMods.Library.Tests.DownloadsService.Helpers;
using NexusMods.Sdk.Jobs;
using NexusMods.Sdk.NexusModsApi;
using Xunit;
using SyncHelpers = NexusMods.Library.Tests.DownloadsService.Helpers.SynchronizationHelpers;

namespace NexusMods.Library.Tests.DownloadsService;

public abstract class DownloadsServiceTestsBase(
    IJobMonitor jobMonitor,
    Library.DownloadsService service,
    IServiceProvider serviceProvider)
{
    protected readonly DownloadJobFactory _jobFactory = new(jobMonitor, serviceProvider);

    // Helper methods
    protected CompositeDisposable SetupCollectionSubscriptions(
        out List<DownloadInfo> allDownloads,
        out List<DownloadInfo> completedDownloads,
        out List<DownloadInfo> activeDownloads,
        out List<DownloadInfo>? gameDownloads,
        NexusModsGameId? gameId = null)
    {
        var disposables = new CompositeDisposable();
        
        // Create local lists first
        var localAllDownloads = new List<DownloadInfo>();
        var localCompletedDownloads = new List<DownloadInfo>();
        var localActiveDownloads = new List<DownloadInfo>();
        var localGameDownloads = gameId.HasValue ? new List<DownloadInfo>() : null;
        
        service.AllDownloads.Subscribe(changes => 
        {
            foreach (var change in changes)
            {
                switch (change.Reason)
                {
                    case ChangeReason.Add:
                    case ChangeReason.Update:
                        localAllDownloads.Add(change.Current);
                        break;
                    case ChangeReason.Remove:
                        localAllDownloads.RemoveAll(d => d.Id == change.Key);
                        break;
                }
            }
        }).DisposeWith(disposables);
        
        service.CompletedDownloads.Subscribe(changes => 
        {
            foreach (var change in changes)
            {
                switch (change.Reason)
                {
                    case ChangeReason.Add:
                    case ChangeReason.Update:
                        localCompletedDownloads.Add(change.Current);
                        break;
                    case ChangeReason.Remove:
                        localCompletedDownloads.RemoveAll(d => d.Id == change.Key);
                        break;
                }
            }
        }).DisposeWith(disposables);
        
        service.ActiveDownloads.Subscribe(changes => 
        {
            foreach (var change in changes)
            {
                switch (change.Reason)
                {
                    case ChangeReason.Add:
                    case ChangeReason.Update:
                        localActiveDownloads.Add(change.Current);
                        break;
                    case ChangeReason.Remove:
                        localActiveDownloads.RemoveAll(d => d.Id == change.Key);
                        break;
                }
            }
        }).DisposeWith(disposables);
        
        if (gameId.HasValue && localGameDownloads != null)
        {
            service.GetDownloadsForGame(gameId.Value).Subscribe(changes => 
            {
                foreach (var change in changes)
                {
                    switch (change.Reason)
                    {
                        case ChangeReason.Add:
                        case ChangeReason.Update:
                            localGameDownloads.Add(change.Current);
                            break;
                        case ChangeReason.Remove:
                            localGameDownloads.RemoveAll(d => d.Id == change.Key);
                            break;
                    }
                }
            }).DisposeWith(disposables);
        }
        
        // Assign to out parameters
        allDownloads = localAllDownloads;
        completedDownloads = localCompletedDownloads;
        activeDownloads = localActiveDownloads;
        gameDownloads = localGameDownloads;
        
        return disposables;
    }
    
    protected CompositeDisposable SetupCollectionSubscriptions(
        out List<DownloadInfo> allDownloads,
        out List<DownloadInfo> completedDownloads,
        out List<DownloadInfo> activeDownloads)
    {
        return SetupCollectionSubscriptions(out allDownloads, out completedDownloads, out activeDownloads, out _);
    }
    
}

public class DownloadsServiceTests(
    IJobMonitor jobMonitor,
    Library.DownloadsService service,
    IServiceProvider serviceProvider)
    : DownloadsServiceTestsBase(jobMonitor, service, serviceProvider)
{
    [Fact]
    public async Task Validate_Download_Jobs_Lifetime()
    {
        // Arrange
        var gameId = NexusModsGameId.From(1234u);
        
        // Subscribe to collections - SourceCache publishes immediately on subscribe
        using var disposables = SetupCollectionSubscriptions(
            out var allDownloads,
            out var completedDownloads,
            out var activeDownloads,
            out var gameDownloads,
            gameId);

        // 1. No jobs initially
        allDownloads.Should().BeEmpty("no jobs should exist initially");
        completedDownloads.Should().BeEmpty("no completed jobs should exist initially");
        activeDownloads.Should().BeEmpty("no active jobs should exist initially");
        gameDownloads!.Should().BeEmpty("no game-specific jobs should exist initially");
        
        // 2. Start job with signals for proper synchronization
        var context = _jobFactory.CreateAndStartDownloadJob(gameId);
        
        // Wait for jobs to signal they're ready before checking state
        context.WaitForJobsReady(TimeSpan.FromSeconds(30))
            .Should().BeTrue("jobs should signal ready within timeout");
        
        // Signal jobs to start and wait for collections to be updated
        context.SignalJobsToStart();
        
        // Wait for job to appear in collections with proper timeout
        (await SyncHelpers.WaitForCollectionCount(allDownloads, 1, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("job should be in AllDownloads when started");
        (await SyncHelpers.WaitForCollectionCount(gameDownloads!, 1, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("job should be in game-specific downloads when started");
        (await SyncHelpers.WaitForCollectionCount(activeDownloads, 1, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("job should be in ActiveDownloads when started");
        completedDownloads.Should().BeEmpty("job should not be in CompletedDownloads when started");
        
        // 3. Complete job - should move to CompletedDownloads only
        context.CompleteJob();
        await context.JobTask.Job.WaitAsync();
        
        // Wait for completion to be processed by collections
        (await SyncHelpers.WaitForCollectionCount(completedDownloads, 1, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("completed job should be in CompletedDownloads");
        (await SyncHelpers.WaitForCollectionCount(activeDownloads, 0, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("completed job should not be in ActiveDownloads");
        
        // Verify final state
        allDownloads.Should().HaveCount(1, "completed job should remain in AllDownloads");
        completedDownloads.Should().HaveCount(1, "completed job should be in CompletedDownloads");
        activeDownloads.Should().BeEmpty("completed job should not be in ActiveDownloads");
        gameDownloads.Should().HaveCount(1, "completed job should remain in game-specific downloads");
    }
    
    // Nested Startup class for Xunit.DependencyInjection
    public class Startup
    {
        // https://github.com/pengweiqhca/Xunit.DependencyInjection?tab=readme-ov-file#3-closest-startup
        // A trick for parallelizing tests with Xunit.DependencyInjection
        public void ConfigureServices(IServiceCollection services) => DIHelpers.ConfigureServices(services);
    }
}
