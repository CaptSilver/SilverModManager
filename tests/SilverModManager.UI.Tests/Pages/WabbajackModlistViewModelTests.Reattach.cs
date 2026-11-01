using System.Reactive.Linq;
using FluentAssertions;
using SilverModManager.Sdk.Games;
using SilverModManager.Wabbajack.Acquire;
using Microsoft.Extensions.DependencyInjection;
using SilverModManager.App.UI.Pages.Collections;
using SilverModManager.App.UI.Pages.Wabbajack;
using SilverModManager.Sdk.Errors;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using Avalonia.Threading;
using SilverModManager.UI.Tests.Pages.Collections;
using SilverModManager.App.UI.Pages.LoadoutPage;
using SilverModManager.App.UI.Windows;
using NSubstitute;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>An install that is already going when the page opens, because the user left the page and came back.</summary>
public partial class WabbajackModlistViewModelTests
{
    private static ModlistInstallProgress.Snapshot Writing(int done, int total, long doneBytes = 0, long totalBytes = 0)
        => ModlistInstallProgress.Snapshot.Empty with { Phase = InstallPhase.Write, DoneItems = done, TotalItems = total, DoneBytes = doneBytes, TotalBytes = totalBytes };

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(WabbajackModlistViewModel Vm, FakeService Service, FakeRun Run)> OpenWhileInstalling(IServiceProvider? services = null, bool unfinishedGroup = true)
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)), load: false, services: services);
        service.Running = new FakeRun();
        if (unfinishedGroup)
        {
            var loadout = await SeedLoadoutRow(GameStore.Steam, "Installing into");
            vm.PinnedLoadout = loadout.LoadoutId;
            await WabbajackGroupSeed.AddGroup(Connection, SomeFile.Value, loadout.LoadoutId, "Half written");
        }
        return (vm, service, service.Running);
    }

    [Fact]
    public async Task APageOpenedWhileTheInstallRunsShowsItsProgressAndOffersNeitherResumeNorAnotherStart()
    {
        var (vm, service, run) = await OpenWhileInstalling();
        using var activation = vm.Activator.Activate();
        await vm.LoadAsync(CancellationToken.None);

        run.Snapshots.OnNext(Writing(3, 10));

        await EventuallyOnUi(() =>
        {
            vm.IsInstalling.Should().BeTrue();
            vm.ProgressText.Should().Be("Writing files: 3 of 10");
            vm.ProgressFraction.Should().BeApproximately(0.3, 1e-9);
            vm.IsInstalled.Should().BeTrue("the half-written group was read");
            vm.CanResume.Should().BeFalse("a second install would write into the loadout the running one writes into");
            vm.CanInstall.Should().BeFalse();
            vm.InstallBlockedReason.Should().Be("An install is running.");
            vm.CanDownload.Should().BeFalse();
            vm.DownloadBlockedReason.Should().Be("An install is running.");
        });
        service.Installs.Should().BeEmpty("the page found the run, it did not start one");

        run.Done.SetResult();
        await EventuallyOnUi(() =>
        {
            vm.IsInstalling.Should().BeFalse();
            vm.CanResume.Should().BeTrue("the group is still unfinished, so Resume comes back once the run is over");
        });
    }

    [Fact]
    public async Task ThePageFindsTheRunOnActivationBeforeAnythingIsLoaded()
    {
        var (vm, _, run) = await OpenWhileInstalling();
        run.Snapshots.OnNext(Writing(1, 4));

        using var activation = vm.Activator.Activate();

        await EventuallyOnUi(() =>
        {
            vm.IsInstalling.Should().BeTrue("the pinned loadout is known before the list is read");
            vm.ProgressText.Should().Be("Writing files: 1 of 4");
        });
    }

    [Fact]
    public async Task WhenTheRunFinishesThePageRefreshesItsInstalledStateAsAfterItsOwnRun()
    {
        var (vm, _, run) = await OpenWhileInstalling(unfinishedGroup: false);
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Finishing");
        vm.PinnedLoadout = loadout.LoadoutId;
        var group = await WabbajackGroupSeed.AddGroup(Connection, SomeFile.Value, loadout.LoadoutId, "Finishing list");
        using var activation = vm.Activator.Activate();
        await vm.LoadAsync(CancellationToken.None);
        run.Snapshots.OnNext(Writing(2, 4));
        await EventuallyOnUi(() => vm.IsInstalling.Should().BeTrue());

        await WabbajackGroupSeed.Finish(Connection, group);
        run.Done.SetResult();

        await EventuallyOnUi(() =>
        {
            vm.IsInstalling.Should().BeFalse();
            vm.ProgressText.Should().BeEmpty();
            vm.IsInstalled.Should().BeTrue();
            vm.CanResume.Should().BeFalse();
        });
    }

    [Fact]
    public async Task AFailedRunTheLoadedPageFoundShowsItsMessage()
    {
        var (vm, _, run) = await OpenWhileInstalling();
        using var activation = vm.Activator.Activate();
        await vm.LoadAsync(CancellationToken.None);

        run.Done.SetException(new InvalidOperationException("disk full"));

        await EventuallyOnUi(() =>
        {
            vm.IsInstalling.Should().BeFalse();
            vm.ErrorText.Should().Be("disk full");
        });
    }

    [Fact]
    public async Task ARunThatIsOverWhenThePageOpensIsNotFollowed()
    {
        var (vm, _, run) = await OpenWhileInstalling();
        run.Done.SetResult();

        using var activation = vm.Activator.Activate();
        await vm.LoadAsync(CancellationToken.None);

        vm.IsInstalling.Should().BeFalse();
        vm.ProgressText.Should().BeEmpty();
    }

    [Fact]
    public async Task APressOfInstallThatTheInstallerRefusesShowsTheRefusalAndStaysOff()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        service.StartRefusal = new InstallAlreadyRunningException();

        await vm.InstallCommand.Execute().FirstAsync();

        vm.ErrorText.Should().Be("An install into this loadout is already running");
        vm.IsInstalling.Should().BeFalse();
    }

    [Fact]
    public async Task APageThatStartedTheInstallAndOneThatFoundItFollowTheSameRunOnce()
    {
        var (vm, service, _) = await Open(Plan(Row(ArchiveState.Have)));
        vm.InstallCommand.Execute().Subscribe();
        await EventuallyOnUi(() => vm.IsInstalling.Should().BeTrue());
        service.Run.Snapshots.OnNext(Writing(5, 10));
        await EventuallyOnUi(() => vm.ProgressText.Should().Be("Writing files: 5 of 10"));

        vm.AttachToRunningInstall();

        vm.ProgressText.Should().Be("Writing files: 5 of 10", "finding the run it already shows changes nothing");
        service.Run.Done.SetResult();
        await EventuallyOnUi(() => vm.IsInstalling.Should().BeFalse());
    }

    [Fact]
    public async Task TheTimeLeftAppearsOnlyOnceAMinuteOfTheInstallHasBeenSeen()
    {
        var time = new ManualTime();
        var (vm, _, run) = await OpenWhileInstalling(new OverridingServices(Provider, typeof(TimeProvider), time));
        using var activation = vm.Activator.Activate();
        await vm.LoadAsync(CancellationToken.None);

        run.Snapshots.OnNext(Writing(0, 600));
        time.Now = time.Now.AddSeconds(30);
        run.Snapshots.OnNext(Writing(30, 600));
        await EventuallyOnUi(() => vm.ProgressText.Should().Be("Writing files: 30 of 600"));
        vm.InstallEtaText.Should().BeEmpty("30 seconds is not enough to estimate from");

        time.Now = time.Now.AddSeconds(30);
        run.Snapshots.OnNext(Writing(60, 600));

        await EventuallyOnUi(() => vm.InstallEtaText.Should().Be("about 9 min left"));
    }

    [Fact]
    public async Task TheCollectionHeaderShowsTheDownloadsModelsInstallWhicheverTabIsOpen()
    {
        var run = new FakeRun();
        var service = new FakeService(TestGame) { Plan = Plan(Row(ArchiveState.Have)), Running = run };
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Header");
        var file = await CollectionSeed.AddModlistFile(Connection, $"Header list {Guid.NewGuid():N}", version: "1", author: "A", archiveCount: 1, archiveSize: NexusMods.Paths.Size.GB);
        var model = new WabbajackModlistViewModel(Provider.GetRequiredService<IWindowManager>(), Connection, service, new FakeAcquirer(), Provider);
        var source = new WabbajackModlistSource(new OverridingServices(Provider, typeof(IWabbajackModlistViewModel), model), loadout.LoadoutId, file, service);
        var page = await Dispatcher.UIThread.InvokeAsync(() => new CollectionLoadoutViewModel(Substitute.For<IWindowManager>(), Provider, source));
        using var activation = await Dispatcher.UIThread.InvokeAsync(() => page.Activator.Activate());

        run.Snapshots.OnNext(Writing(7, 14));

        foreach (var tab in new[] { CollectionPageTab.Installed, CollectionPageTab.Instructions, CollectionPageTab.Downloads })
        {
            await Dispatcher.UIThread.InvokeAsync(() => page.SelectedTab = tab);
            await EventuallyOnUi(() =>
            {
                page.InstallProgress.Should().NotBeNull($"the {tab} tab shows the install too");
                page.InstallProgress!.Text.Should().Be("Writing files: 7 of 14");
                page.InstallProgress.Fraction.Should().BeApproximately(0.5, 1e-9);
            });
        }

        run.Done.SetResult();
        await EventuallyOnUi(() => page.InstallProgress.Should().BeNull("the header clears when the install ends"));
    }
}
