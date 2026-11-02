using FluentAssertions;
using SilverModManager.Wabbajack.Acquire;

namespace SilverModManager.UI.Tests.Pages;

// What a caller outside the page (the more menu's Reinstall) gets from the model before it starts an install.
public partial class WabbajackModlistViewModelTests
{
    [Fact]
    public async Task PrepareInstall_ChecksAModelWhoseTabWasNeverShown()
    {
        var (vm, _, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        vm.CanInstall.Should().BeFalse("nothing was checked yet");

        await vm.PrepareInstallAsync(CancellationToken.None);

        vm.CanInstall.Should().BeTrue();
    }

    [Fact]
    public async Task PrepareInstall_WaitsForACheckThatIsAlreadyRunning()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;
        var loading = vm.LoadAsync(CancellationToken.None);
        await UntilPlanning(service);

        var preparing = vm.PrepareInstallAsync(CancellationToken.None);
        await Task.Delay(100);
        preparing.IsCompleted.Should().BeFalse("the plan is not there yet");

        gate.SetResult();
        await preparing;
        await loading;

        vm.CanInstall.Should().BeTrue();
    }

    [Fact]
    public async Task PrepareInstall_DoesNotCheckAgainAModelThatWasChecked()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        var plans = service.PlannedFor.Count;

        await vm.PrepareInstallAsync(CancellationToken.None);

        service.PlannedFor.Count.Should().Be(plans);
    }
}
