using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Library;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Install;
using SilverModManager.UI.Tests.Pages.Collections;

namespace SilverModManager.UI.Tests.Pages;

public partial class WabbajackModlistViewModelTests
{
    [Fact]
    public async Task ASourceWithoutALoadoutFollowsTheLoadoutThePickerHolds()
    {
        var (vm, _, _) = await Open(Plan());
        var picked = vm.SelectedLoadout!.Id!.Value;
        var file = await CollectionSeed.AddModlistFile(Connection, $"Picker list {Guid.NewGuid():N}");
        var group = await WabbajackGroupSeed.AddGroup(Connection, file.Id, picked, file.Name);
        await WabbajackGroupSeed.Finish(Connection, group);
        var services = new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm);
        var source = new WabbajackModlistSource(services, loadoutId: null, file);
        source.CreateDownloads().Should().BeSameAs(vm);

        (await source.Group.FirstAsync()).Value.Value.Should().Be(group, "the picker starts on the first loadout of the game, which holds the install");
        (await source.IsInstalled.FirstAsync()).Should().BeTrue();

        await vm.SelectLoadoutAsync(WabbajackLoadoutChoice.CreateNew, CancellationToken.None);
        (await source.Group.FirstAsync()).HasValue.Should().BeFalse("a loadout that does not exist yet holds no install");
        (await source.StateLine.FirstAsync()).Should().Be("Not installed yet");
        (await source.CanToggle.FirstAsync()).Should().BeFalse();

        await vm.SelectLoadoutAsync(vm.LoadoutChoices.First(choice => choice.Id == picked), CancellationToken.None);
        (await source.Group.FirstAsync()).Value.Value.Should().Be(group);
    }

    [Fact]
    public async Task TheHeaderShowsTheInstallProgressAndTheTabDoesNotRepeatItNorMixesTwoSnapshots()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        var file = await CollectionSeed.AddModlistFile(Connection, $"Progress list {Guid.NewGuid():N}");
        var services = new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm);
        var source = new WabbajackModlistSource(services, loadoutId: null, file);
        source.CreateDownloads();
        var header = new List<CollectionInstallStatus>();
        using var subscription = source.InstallProgress.Subscribe(status => { if (status is not null) header.Add(status); });
        vm.InstallCommand.Execute().Subscribe();

        var snapshots = new[]
        {
            ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = 12, TotalItems = 40 },
            ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Resolve, DoneItems = 25871, TotalItems = 51774 },
            ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = 3, TotalItems = 10 },
        };
        foreach (var snapshot in snapshots)
        {
            service.Run.Snapshots.OnNext(snapshot);

            var shown = header[^1];
            shown.Text.Should().Be(WabbajackModlistText.Progress(snapshot));
            shown.Fraction.Should().BeApproximately(WabbajackModlistText.Fraction(snapshot), 1e-9);
            vm.Status.Should().Be(new WabbajackStatus("", null, false, false), "the header shows the install progress, so the Downloads tab does not repeat it");
        }

        var expected = snapshots.Select(snapshot => (WabbajackModlistText.Progress(snapshot), WabbajackModlistText.Fraction(snapshot))).ToHashSet();
        header.Should().OnlyContain(status => status.Text == "Checking the list" || expected.Any(pair => pair.Item1 == status.Text && Math.Abs(pair.Item2 - status.Fraction) < 1e-9),
            "an emission that pairs one snapshot's text with another's fraction would draw the wrong bar for a moment");
        service.Run.Done.SetResult();
    }

    [Fact]
    public async Task TheHeaderShowsTheCheckAndItsBarThroughTheSameStatusAsTheInstall()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false);
        var file = await CollectionSeed.AddModlistFile(Connection, $"Check list {Guid.NewGuid():N}");
        var source = new WabbajackModlistSource(new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm), loadoutId: null, file);
        source.CreateDownloads();
        CollectionInstallStatus? header = null;
        using var subscription = source.InstallProgress.Subscribe(status => header = status);
        var gate = new TaskCompletionSource();
        service.PlanGate = gate;

        var loading = vm.LoadAsync(CancellationToken.None);
        await UntilPlanning(service);
        header.Should().NotBeNull();
        header!.Text.Should().Be(vm.CheckText);
        header.IsIndeterminate.Should().BeTrue("nothing has been counted yet");

        service.LastProgress!.Report(new PlanProgress(PlanPhase.CheckingLibrary, 412, 1294));
        header!.Text.Should().Be("Checking archives against your library: 412 of 1294");
        header.IsIndeterminate.Should().BeFalse();
        header.Fraction.Should().BeApproximately(412d / 1294, 1e-9);
        vm.Status.Text.Should().BeEmpty("the tab says nothing while the header does");

        gate.SetResult();
        await loading;
        header.Should().BeNull("the header clears when the check ends");
    }

    [Fact]
    public async Task TheHeaderShowsDownloadProgressWhileArchivesAreComing()
    {
        var (vm, _, acquirer) = await Open(Plan(Row(ArchiveState.Automatic)), runUnderWay: null);
        using var activation = vm.Activator.Activate();
        var file = await CollectionSeed.AddModlistFile(Connection, $"Download list {Guid.NewGuid():N}");
        var source = new WabbajackModlistSource(new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), vm), loadoutId: null, file);
        source.CreateDownloads();
        CollectionInstallStatus? header = null;
        using var subscription = source.InstallProgress.Subscribe(status => header = status);

        acquirer.ChangeStatus(new AcquireStatus(vm.FileId, true, 1, 4, 0, TimeSpan.Zero, []));

        await EventuallyOnUi(() =>
        {
            vm.Downloads.Should().Be(new AcquireProgress(1, 4));
            vm.RunningStatus.Text.Should().Be("Downloading archives: 1 of 4");
            header.Should().Be(new CollectionInstallStatus("Downloading archives: 1 of 4", 0.25));
            vm.Status.Text.Should().NotContain("Downloading archives");
        });
    }
}
