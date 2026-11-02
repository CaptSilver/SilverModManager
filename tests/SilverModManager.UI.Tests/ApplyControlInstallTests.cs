using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Pages.Collections;
using SilverModManager.Wabbajack.Install;
using SilverModManager.Wabbajack.Models;
using System.Reactive.Linq;

namespace SilverModManager.UI.Tests;

/// <summary>The Apply button while a modlist install writes into the loadout.</summary>
public class ApplyControlInstallTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>Hands the view model a synchronizer that calls the loadout out of date, so Apply is on unless something else turns it off.</summary>
    private sealed class OutdatedSyncProvider(IServiceProvider inner) : IServiceProvider
    {
        private readonly ISynchronizerService _sync = CreateSync();

        private static ISynchronizerService CreateSync()
        {
            var sync = Substitute.For<ISynchronizerService>();
            sync.StatusForLoadout(Arg.Any<LoadoutId>()).Returns(Task.FromResult(Observable.Return(LoadoutSynchronizerState.NeedsSync)));
            sync.StatusForGame(Arg.Any<GameInstallMetadataId>()).Returns(Observable.Return(GameSynchronizerState.Idle));
            return sync;
        }

        public object? GetService(Type serviceType) => serviceType == typeof(ISynchronizerService) ? _sync : inner.GetService(serviceType);
    }

    private async Task<ApplyControlViewModel> OpenFor(Loadout.ReadOnly loadout)
    {
        var vm = await OnUi(() => Task.FromResult(new ApplyControlViewModel(
            loadout.LoadoutId, new OutdatedSyncProvider(Provider), Provider.GetRequiredService<IJobMonitor>(),
            Substitute.For<IOverlayController>(), Provider.GetRequiredService<GameRunningTracker>())));
        await OnUi(() => { vm.Activator.Activate(); });
        return vm;
    }

    private async Task<bool> CanApply(ApplyControlViewModel vm)
        => await OnUi(() => Task.FromResult(vm.ApplyCommand.CanExecute.FirstAsync().GetAwaiter().GetResult()));

    private async Task<(IJobTask<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly> Task, TaskCompletionSource Gate)> BeginInstall(
        Loadout.ReadOnly loadout, Func<IJobContext<InstallModlistJob>, ValueTask<WabbajackModlistLoadoutGroup.ReadOnly>>? body = null)
    {
        var file = await CollectionSeed.AddModlistFile(Connection, $"Install list {Guid.NewGuid():N}");
        var job = new InstallModlistJob
        {
            TargetLoadout = loadout.LoadoutId,
            Modlist = file,
            Options = new InstallOptions(false, new HashSet<RelativePath>()),
            Connection = null!, FileStore = null!, Inventory = null!, Transforms = null!, PrefixResolver = null!,
            Paths = null!, Writer = null!, Openers = null!, Logger = null!, OrderImporter = null!,
        };
        var gate = new TaskCompletionSource();
        var task = Provider.GetRequiredService<IJobMonitor>().Begin<InstallModlistJob, WabbajackModlistLoadoutGroup.ReadOnly>(job,
            body ?? (async _ =>
            {
                await gate.Task;
                return default;
            }));
        return (task, gate);
    }

    [Fact]
    public async Task ApplyIsOffWhileAnInstallIntoTheLoadoutRunsAndOnceItCompletes()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, $"Installing {Guid.NewGuid():N}");
        var vm = await OpenFor(loadout);
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue("nothing else holds Apply back"));

        var (install, gate) = await BeginInstall(loadout);
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeFalse("a half-installed list must not be synced"));

        gate.SetResult();
        await install;
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue());
    }

    [Fact]
    public async Task ApplyComesBackWhenTheInstallFails()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, $"Failing {Guid.NewGuid():N}");
        var vm = await OpenFor(loadout);
        var fail = new TaskCompletionSource();
        var (install, _) = await BeginInstall(loadout, async _ =>
        {
            await fail.Task;
            throw new InvalidOperationException("boom");
        });
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeFalse());

        fail.SetResult();
        await Record.ExceptionAsync(async () => await install);

        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue());
    }

    [Fact]
    public async Task ApplyComesBackWhenTheInstallIsCancelled()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, $"Cancelled {Guid.NewGuid():N}");
        var vm = await OpenFor(loadout);
        var (install, _) = await BeginInstall(loadout, async ctx =>
        {
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return default;
        });
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeFalse());

        Provider.GetRequiredService<IJobMonitor>().Cancel(install);
        await Record.ExceptionAsync(async () => await install);

        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue());
    }

    [Fact]
    public async Task AnInstallIntoAnotherLoadoutLeavesApplyOn()
    {
        var mine = await SeedLoadoutRow(GameStore.Unknown, $"Mine {Guid.NewGuid():N}");
        var other = await SeedLoadoutRow(GameStore.Unknown, $"Other {Guid.NewGuid():N}");
        var vm = await OpenFor(mine);

        var (install, gate) = await BeginInstall(other);

        await Task.Delay(300);
        (await CanApply(vm)).Should().BeTrue();
        gate.SetResult();
        await install;
    }
}
