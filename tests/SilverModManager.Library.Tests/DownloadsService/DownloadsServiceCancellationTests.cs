using System.Reactive.Disposables;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Library.Tests.DownloadsService.Helpers;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.NexusModsApi;
using Xunit;
using SyncHelpers = SilverModManager.Library.Tests.DownloadsService.Helpers.SynchronizationHelpers;

namespace SilverModManager.Library.Tests.DownloadsService;

public class DownloadsServiceCancellationTests(
    IJobMonitor jobMonitor,
    Library.DownloadsService service,
    IServiceProvider serviceProvider)
    : DownloadsServiceTestsBase(jobMonitor, service, serviceProvider)
{
    [Fact]
    public async Task CancelledJobs_ShouldBeCompletelyRemoved_FromAllCollections()
    {
        // Arrange
        var gameId = NexusModsGameId.From(1234u);
        
        using var disposables = SetupCollectionSubscriptions(
            out var allDownloads,
            out var completedDownloads,
            out var activeDownloads);
        
        // Initially empty
        allDownloads.Should().BeEmpty("no jobs should exist initially");
        
        // Create and start job
        var context = _jobFactory.CreateAndStartDownloadJob(gameId);
        
        // Wait for jobs to signal they're ready before checking state
        context.WaitForJobsReady(TimeSpan.FromSeconds(30))
            .Should().BeTrue("jobs should signal ready within timeout");
        
        // Job should appear in collections
        allDownloads.Should().HaveCount(1, "job should be in AllDownloads when started");
        activeDownloads.Should().HaveCount(1, "job should be in ActiveDownloads when started");
        
        // Start a pre-Cancelled the job
        context.CancelJob();
        context.SignalJobsToStart();
        
        try
        {
            await context.JobTask.Job.WaitAsync();
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Cancelled jobs should be completely removed
        // Note: The change isn't instant so we must wait for the collections to update
        (await SyncHelpers.WaitForCollectionCount(allDownloads, 0, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("cancelled jobs should be removed from AllDownloads");
        (await SyncHelpers.WaitForCollectionCount(completedDownloads, 0, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("cancelled jobs should not be in CompletedDownloads");
        (await SyncHelpers.WaitForCollectionCount(activeDownloads, 0, TimeSpan.FromSeconds(30)))
            .Should().BeTrue("cancelled jobs should not be in ActiveDownloads");
    }

    // Nested Startup class for Xunit.DependencyInjection
    public class Startup
    {
        // https://github.com/pengweiqhca/Xunit.DependencyInjection?tab=readme-ov-file#3-closest-startup
        // A trick for parallelizing tests with Xunit.DependencyInjection
        public void ConfigureServices(IServiceCollection services) => DIHelpers.ConfigureServices(services);
    }
}
