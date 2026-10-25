using System.Reactive.Linq;
using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ReactiveUI;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Ids;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.App.UI.Controls.LoadoutCard;
using SilverModManager.App.UI.Overlays;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class LoadoutCardSwitchTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed class Fixture
    {
        public LoadoutCardViewModel Card { get; set; } = null!;
        public required ISynchronizerService Synchronizer { get; init; }
        public required Subject<LoadoutWithTxId> LastApplied { get; init; }
        public required Loadout.ReadOnly Loadout { get; init; }
        public int Visits;
    }

    private async Task<Fixture> CardFor()
    {
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Switch " + Guid.NewGuid());
        var synchronizer = Substitute.For<ISynchronizerService>();
        var lastApplied = new Subject<LoadoutWithTxId>();
        synchronizer.LastAppliedRevisionFor(Arg.Any<GameInstallation>()).Returns(lastApplied);
        synchronizer.Synchronize(Arg.Any<LoadoutId>()).Returns(Task.CompletedTask);

        var fixture = new Fixture { Synchronizer = synchronizer, LastApplied = lastApplied, Loadout = loadout };
        fixture.Card = new LoadoutCardViewModel(loadout, Connection, new SynchronizerProvider(Provider, synchronizer))
        {
            VisitLoadoutCommand = ReactiveCommand.Create(() => { Interlocked.Increment(ref fixture.Visits); }),
        };
        return fixture;
    }

    [Fact]
    public async Task ACardThatIsNotApplied_ShowsTheSwitchButton()
    {
        var f = await CardFor();
        using var _ = await OnUi(() => Task.FromResult(f.Card.Activator.Activate()));

        f.LastApplied.OnNext(new LoadoutWithTxId(LoadoutId.From(EntityId.From(1)), default));

        await EventuallyOnUi(() => f.Card.ShowSwitchButton.Should().BeTrue());
    }

    [Fact]
    public async Task TheAppliedCard_HidesTheSwitchButtonAndKeepsItsAppliedState()
    {
        var f = await CardFor();
        using var _ = await OnUi(() => Task.FromResult(f.Card.Activator.Activate()));

        f.LastApplied.OnNext(new LoadoutWithTxId(f.Loadout.LoadoutId, default));

        await EventuallyOnUi(() =>
        {
            f.Card.IsLoadoutApplied.Should().BeTrue();
            f.Card.ShowSwitchButton.Should().BeFalse();
        });
    }

    [Fact]
    public async Task Switching_SynchronizesThatLoadoutThenVisitsIt()
    {
        var f = await CardFor();
        using var _ = await OnUi(() => Task.FromResult(f.Card.Activator.Activate()));

        await OnUi(async () => await f.Card.SwitchToLoadoutCommand.Execute());

        await f.Synchronizer.Received(1).Synchronize(f.Loadout.LoadoutId);
        f.Visits.Should().Be(1);
    }

    [Fact]
    public async Task WhenTheGameIsRunning_TheLoadoutIsNotVisited()
    {
        var f = await CardFor();
        f.Synchronizer.Synchronize(Arg.Any<LoadoutId>()).Returns(Task.FromException(new SilverModManager.Abstractions.Loadouts.Exceptions.ExecutableInUseException("game.exe")));
        using var _ = await OnUi(() => Task.FromResult(f.Card.Activator.Activate()));

        await OnUi(async () => await f.Card.SwitchToLoadoutCommand.Execute());

        f.Visits.Should().Be(0, "the loadout was not applied");
    }

    [Fact]
    public async Task ASynchronizeOnAnotherInstallation_DoesNotDisableSwitching()
    {
        var f = await CardFor();
        // Seeding writes a fresh installation row, so this loadout belongs to a different installation.
        var elsewhere = await SeedLoadoutRow(GameStore.Unknown, "Elsewhere " + Guid.NewGuid());
        elsewhere.InstallationId.Should().NotBe(f.Loadout.InstallationId);
        using var _ = await OnUi(() => Task.FromResult(f.Card.Activator.Activate()));

        var monitor = Provider.GetRequiredService<IJobMonitor>();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var running = monitor.Begin(new SynchronizeLoadoutJob(elsewhere.LoadoutId), async _ =>
        {
            started.SetResult();
            await release.Task;
            return R3.Unit.Default;
        });
        await started.Task;

        try
        {
            await EventuallyOnUi(async () => (await f.Card.SwitchToLoadoutCommand.CanExecute.FirstAsync()).Should().BeTrue());
            // Give a wrongly matching job observer time to flip the command off before checking again.
            await Task.Delay(200);
            (await OnUi(async () => await f.Card.SwitchToLoadoutCommand.CanExecute.FirstAsync())).Should().BeTrue();
        }
        finally
        {
            release.SetResult();
            await running;
        }
    }

    [Fact]
    public async Task WhileASynchronizeRunsOnTheInstallation_SwitchingIsDisabled()
    {
        var f = await CardFor();
        // A second loadout on the same installation is the one being synchronized.
        var sibling = await SiblingOf(f.Loadout);
        using var _ = await OnUi(() => Task.FromResult(f.Card.Activator.Activate()));

        var monitor = Provider.GetRequiredService<IJobMonitor>();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var running = monitor.Begin(new SynchronizeLoadoutJob(sibling), async _ =>
        {
            started.SetResult();
            await release.Task;
            return R3.Unit.Default;
        });
        await started.Task;

        await EventuallyOnUi(async () => (await f.Card.SwitchToLoadoutCommand.CanExecute.FirstAsync()).Should().BeFalse());

        release.SetResult();
        await running;

        await EventuallyOnUi(async () => (await f.Card.SwitchToLoadoutCommand.CanExecute.FirstAsync()).Should().BeTrue());
    }

    private async Task<LoadoutId> SiblingOf(Loadout.ReadOnly loadout)
    {
        using var tx = Connection.BeginTransaction();
        var sibling = new Loadout.New(tx)
        {
            Name = "Sibling " + Guid.NewGuid(),
            ShortName = "ZZY",
            InstallationId = loadout.InstallationId,
            LoadoutKind = LoadoutKind.Default,
            Revision = 0,
            GameVersion = VanityVersion.From("Unknown"),
        };
        var result = await tx.Commit();
        return result.Remap(sibling).LoadoutId;
    }

    /// <summary>
    /// The real services, except that nothing is synchronized and overlays go to a recorder instead of the (absent) window.
    /// </summary>
    private sealed class SynchronizerProvider(IServiceProvider inner, ISynchronizerService synchronizer) : IServiceProvider
    {
        private readonly IOverlayController _overlays = Substitute.For<IOverlayController>();

        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISynchronizerService) ? synchronizer
            : serviceType == typeof(IOverlayController) ? _overlays
            : inner.GetService(serviceType);
    }
}
