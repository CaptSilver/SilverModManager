using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using NSubstitute;
using R3;

namespace SilverModManager.UI.Tests;

public class ApplyControlProgressTests(IServiceProvider provider) : AUiTest(provider)
{
    private async Task<(ApplyControlViewModel Vm, SynchronizeLoadoutJob Job, TaskCompletionSource Release, IJobTask<SynchronizeLoadoutJob, Unit> Running)> StartApplyingViewModel()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "ProgressLoadout");
        var monitor = Provider.GetRequiredService<IJobMonitor>();
        var job = new SynchronizeLoadoutJob(loadout.LoadoutId);
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var running = monitor.Begin(job, async _ =>
        {
            started.SetResult();
            await release.Task;
            return Unit.Default;
        });
        await started.Task;

        var vm = await OnUi(() => Task.FromResult(new ApplyControlViewModel(
            loadout.LoadoutId, Provider, monitor, Substitute.For<IOverlayController>(), Provider.GetRequiredService<GameRunningTracker>())));
        return (vm, job, release, running);
    }

    [Fact]
    public async Task WhileApplying_ExposesThePhaseTextAndFraction()
    {
        var (vm, job, release, running) = await StartApplyingViewModel();
        using var _ = await OnUi(() => Task.FromResult(vm.Activator.Activate()));

        job.ReportProgress(SyncPhases.Deploying, filesDone: 3, filesTotal: 12);

        await EventuallyOnUi(() =>
        {
            vm.ProgressFraction.Should().Be(0.25);
            vm.IsProgressIndeterminate.Should().BeFalse();
            vm.ProcessingText.Should().Contain(SyncPhases.Deploying).And.Contain("3/12");
            return Task.CompletedTask;
        });

        release.SetResult();
        await running;
    }

    [Fact]
    public async Task AnUnknownTotal_ShowsThePhaseAsIndeterminate()
    {
        var (vm, job, release, running) = await StartApplyingViewModel();
        using var _ = await OnUi(() => Task.FromResult(vm.Activator.Activate()));

        job.SetStatus(SyncPhases.RecordingChanges);

        await EventuallyOnUi(() =>
        {
            vm.IsProgressIndeterminate.Should().BeTrue();
            vm.ProcessingText.Should().Be(SyncPhases.RecordingChanges);
            return Task.CompletedTask;
        });

        release.SetResult();
        await running;
    }
}
