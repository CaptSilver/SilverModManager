using System.Reactive.Subjects;
using DynamicData;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.App.UI.Pages.Downloads;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using R3;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>What a row of the Downloads page shows for a download that is not from Nexus Mods.</summary>
public class DownloadRowComponentTests(IServiceProvider provider) : AUiTest(provider)
{
    private static DownloadComponents.StatusComponent StatusFor(JobStatus status, bool supportsPausing, string? failureReason = null)
        => new(Percent.Zero, status, Observable.Return(Percent.Zero), Observable.Return(status), supportsPausing, failureReason, Observable.Return(failureReason));

    [Fact]
    public async Task APauseButtonIsOfferedOnlyForADownloadThatCanPause()
    {
        await OnUi(() =>
        {
            StatusFor(JobStatus.Running, supportsPausing: true).CanPause.Value.Should().BeTrue();
            StatusFor(JobStatus.Running, supportsPausing: false).CanPause.Value.Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AFailedDownloadShowsItsReasonAndOffersNothingToCancel()
    {
        await OnUi(() =>
        {
            var status = StatusFor(JobStatus.Failed, supportsPausing: false, "MEGA answered 509; download it in your browser");

            status.IsFailed.Value.Should().BeTrue();
            status.FailureReason.Value.Should().Be("MEGA answered 509; download it in your browser");
            status.CanCancel.Value.Should().BeFalse();
            status.CanPause.Value.Should().BeFalse();
            status.ShowsProgress.Value.Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ARunningDownloadShowsItsProgressBar()
    {
        await OnUi(() =>
        {
            var status = StatusFor(JobStatus.Running, supportsPausing: false);

            status.IsFailed.Value.Should().BeFalse();
            status.ShowsProgress.Value.Should().BeTrue();
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ADownloadFromAHostShowsItsModlistOrElseItsHostInTheGameColumn()
    {
        var withGroup = new DownloadInfo { Id = DownloadId.From(Guid.NewGuid()) };
        withGroup.SetSourceName("MEGA");
        withGroup.SetGroupName("Some Modlist");
        var alone = new DownloadInfo { Id = DownloadId.From(Guid.NewGuid()) };
        alone.SetSourceName("MEGA");

        var downloads = new SourceCache<DownloadInfo, DownloadId>(x => x.Id);
        downloads.AddOrUpdate([withGroup, alone]);
        var service = Substitute.For<IDownloadsService>();
        service.AllDownloads.Returns(downloads.Connect());
        var services = new ServiceCollection()
            .AddSingleton(service)
            .AddSingleton(Substitute.For<IGameRegistry>())
            .AddSingleton(Connection)
            .BuildServiceProvider();

        var models = await OnUi(() =>
        {
            var found = new List<(DownloadId Id, string Text)>();
            using var subscription = new DownloadsDataProvider(services).ObserveDownloads(DownloadsFilter.All()).Subscribe(changes =>
            {
                foreach (var change in changes)
                    found.Add((change.Key, change.Current.GetOptional<DownloadComponents.GameComponent>(DownloadColumns.Game.ComponentKey).Value.GameName.Value));
            });
            return Task.FromResult(found);
        });

        models.Should().Contain((withGroup.Id, "Some Modlist"));
        models.Should().Contain((alone.Id, "MEGA"));
    }
}
