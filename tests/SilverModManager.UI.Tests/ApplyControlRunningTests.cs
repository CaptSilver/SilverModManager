using System.Collections.Immutable;
using System.Runtime.InteropServices;
using FluentAssertions;
using NexusMods.Paths;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Overlays;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using System.Reactive.Linq;
using Unit = R3.Unit;

namespace SilverModManager.UI.Tests;

/// <summary>Apply while a launch of the loadout's own installation is registered, and not for another game's.</summary>
public class ApplyControlRunningTests(IServiceProvider provider) : AUiTest(provider)
{
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

    [Fact]
    public async Task ApplyIsOffWhileALaunchOfTheLoadoutsInstallationIsRegisteredAndOnceItEnds()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, $"Running {Guid.NewGuid():N}");
        var vm = await OpenFor(loadout);
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue("nothing else holds Apply back"));

        var registration = Provider.GetRequiredService<ActiveLaunchSessions>().Register(loadout.InstallationInstance);
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeFalse("the game's files are in use"));

        registration.Dispose();
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue());
    }

    /// <summary>An installation of a different game: every stub locator reports the same store id, so the key is changed by hand.</summary>
    private static GameInstallation AnotherGamesInstallation(GameInstallation like)
    {
        var result = new GameLocatorResult
        {
            Game = like.Game,
            Path = like.LocatorResult.Path,
            Store = like.LocatorResult.Store,
            StoreIdentifier = "another-game",
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Linux,
        };
        return new GameInstallation(result, like.Locations);
    }

    [Fact]
    public async Task ApplyIsNotDisabledByAnotherGamesLaunch()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, $"Idle {Guid.NewGuid():N}");
        var vm = await OpenFor(loadout);
        await EventuallyOnUi(async () => (await CanApply(vm)).Should().BeTrue());

        // A launch from the app is a run-game job as well as a registration; neither belongs to this installation.
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = Provider.GetRequiredService<IJobMonitor>().Begin<ITool, Unit>(Substitute.For<IRunGameTool>(), async _ =>
        {
            await finish.Task;
            return Unit.Default;
        });
        try
        {
            using var other = Provider.GetRequiredService<ActiveLaunchSessions>()
                .Register(AnotherGamesInstallation(loadout.InstallationInstance));

            await Task.Delay(300);
            (await CanApply(vm)).Should().BeTrue("another game's launch does not touch this game's files");
        }
        finally
        {
            finish.SetResult();
            await job;
        }
    }
}
