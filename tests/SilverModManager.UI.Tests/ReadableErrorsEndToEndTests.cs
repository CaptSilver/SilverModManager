using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using SilverModManager.App.UI;
using SilverModManager.App.UI.Controls.GameWidget;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SilverModManager.App.UI.Dialog;
using SilverModManager.UI.Sdk;
using Unit = R3.Unit;
using ReactiveUI;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Logging;
using SilverModManager.App.UI.Controls.MiniGameWidget.Standard;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Pages.MyGames;
using SilverModManager.App.UI.Windows;
using SilverModManager.Backend.Jobs;
using SilverModManager.DataModel;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Tests.Helpers;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

/// <summary>
/// Play, Apply and Add game from the click down to the dialog, with the pieces in between real: the job monitor
/// that logs a failed job, the tool manager, the presenter, and the pipe that carries Error events from the log
/// back to the presenter the way the app's logging target does. Each failure has to reach the user as one dialog.
/// </summary>
public class ReadableErrorsEndToEndTests(IServiceProvider provider) : AUiTest(provider)
{
    private sealed record FailingJob : IJobDefinition<Unit>;

    /// <summary>
    /// The real presenter, connected to the log pipe, over a job monitor whose log lines go through the same
    /// pipe. Only Error and Fatal lines are forwarded, as in the app.
    /// </summary>
    private sealed class Wiring
    {
        private readonly System.Reactive.Subjects.Subject<LogMessage> _pipe = new();
        private readonly List<LogMessage> _forwarded = [];

        public PresenterRig Presenter { get; }
        public JobMonitor Jobs { get; }
        public IDesktopNotifier Notifier { get; } = Substitute.For<IDesktopNotifier>();
        public IWindowNotificationService Toasts { get; } = Substitute.For<IWindowNotificationService>();

        /// <summary>What the pipe carried to the presenter: the Error and Fatal log lines.</summary>
        public LogMessage[] ErrorEvents
        {
            get { lock (_forwarded) return _forwarded.ToArray(); }
        }

        public Wiring(IServiceProvider provider)
        {
            Presenter = new PresenterRig(provider);
            var jobLog = new RecordingLogger<JobMonitor>();
            jobLog.Written += Forward;
            Presenter.Log.Written += Forward;
            Jobs = new JobMonitor(jobLog);
            Presenter.Presenter.Connect(_pipe);
        }

        private void Forward(LogLevel level, Exception? exception, string message)
        {
            if (level < LogLevel.Error) return;
            var line = new LogMessage(exception, message);
            lock (_forwarded) _forwarded.Add(line);
            _pipe.OnNext(line);
        }

        public IServiceProvider With(params (Type Service, object Instance)[] services) =>
            new OverridingServiceProvider(Presenter.Services, [(typeof(IDesktopNotifier), Notifier), (typeof(IWindowNotificationService), Toasts), .. services]);

        /// <summary>
        /// Runs a job that fails the way a real one does: after it has done some work, and awaited by the caller.
        /// The delay is what lets the caller reach its await before the failure is logged.
        /// </summary>
        public async Task RunFailingJob(Exception failure)
        {
            await Jobs.Begin<FailingJob, Unit>(new FailingJob(), async _ =>
            {
                await Task.Delay(100);
                throw failure;
            });
        }
    }

    private sealed class FakeRunTool(GameId game, Exception failure) : IRunGameTool
    {
        public IEnumerable<GameId> GameIds => [game];
        public string Name => "Fake run";
        public System.IObservable<bool> WaitingForSteam { get; } = new System.Reactive.Subjects.BehaviorSubject<bool>(false);

        public ValueTask<LaunchSpec> GetLaunchSpec(Loadout.ReadOnly loadout, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IJobTask<ITool, Unit> StartJob(Loadout.ReadOnly loadout, IJobMonitor monitor, CancellationToken cancellationToken) =>
            monitor.Begin<ITool, Unit>(this, async _ =>
            {
                await Task.Delay(100, cancellationToken);
                throw failure;
            });
    }

    private static ButtonDefinitionId[] ButtonsOf(DialogViewModel dialog) => dialog.ButtonDefinitions.Select(b => b.Id).ToArray();

    private async Task<(LaunchButtonViewModel ViewModel, Wiring Wiring)> PlayButton(Exception launchFailure, Func<LoadoutId, Exception>? synchronizeFailure = null)
    {
        var wiring = new Wiring(Provider);
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Play " + Guid.NewGuid());

        var synchronizer = Substitute.For<ISynchronizerService>();
        if (synchronizeFailure is not null)
            synchronizer.Synchronize(Arg.Any<LoadoutId>()).Returns<Task>(_ => wiring.RunFailingJob(synchronizeFailure(loadout.LoadoutId)));

        // The real tool manager: it applies the loadout, then starts the tool, as a click on Play does.
        var tool = new FakeRunTool(loadout.Game.GameId, launchFailure);
        var toolManager = new ToolManager(NullLogger<ToolManager>.Instance, [tool], [], synchronizer);

        var viewModel = new LaunchButtonViewModel(
            NullLogger<ILaunchButtonViewModel>.Instance,
            toolManager,
            Connection,
            wiring.Jobs,
            wiring.With((typeof(ISynchronizerService), synchronizer)),
            Provider.GetRequiredService<GameRunningTracker>())
        {
            LoadoutId = loadout.LoadoutId,
        };
        return (viewModel, wiring);
    }

    private static async Task Play(LaunchButtonViewModel viewModel)
    {
        await viewModel.Command.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(10));
        // A second report of the same failure would come from the log a moment after the first dialog.
        await Task.Delay(300);
    }

    [Fact]
    public async Task APlayRefusedByTheSteamUpdateVeto_IsOnePlainDialogWithNoErrorEvent()
    {
        var (button, wiring) = await PlayButton(new GameLaunchException("Steam is updating Cyberpunk 2077. Start it again when the update has finished."));

        await Play(button);

        var dialog = wiring.Presenter.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The game was not started");
        PresenterRig.Body(dialog).Should().Contain("Steam is updating Cyberpunk 2077");
        ButtonsOf(dialog).Should().NotContain(ErrorPresenter.OpenHealthCheckButton);
        wiring.ErrorEvents.Should().BeEmpty();
        wiring.Toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
        wiring.Presenter.AllOnUiThread.Should().BeTrue();
    }

    [Fact]
    public async Task APlayWhoseLoadoutCouldNotBeTakenOffAfterTheGame_IsOnePlainDialogNotACrash()
    {
        var cause = new InvalidOperationException("ingest broke");
        var (button, wiring) = await PlayButton(new LoadoutStillAppliedException(
            "Cyberpunk 2077 has exited, but taking the game's changes back into the loadout failed: ingest broke Loadout 'Mine' is still applied. " +
            "Apply or revert it from SilverModManager before playing the game outside it.", cause));

        await Play(button);

        var dialog = wiring.Presenter.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The loadout is still applied");
        var body = PresenterRig.Body(dialog);
        body.Should().Contain("Loadout 'Mine' is still applied").And.Contain("Apply or revert it");
        body.Should().NotContain("written to the log").And.NotContain("   at ");
        ButtonsOf(dialog).Should().NotContain(ErrorPresenter.OpenHealthCheckButton);
        wiring.ErrorEvents.Should().BeEmpty();
        wiring.Presenter.AllOnUiThread.Should().BeTrue();
    }

    [Fact]
    public async Task APlayRefusedByPreflight_IsOneDialogWithOpenHealthCheck()
    {
        var (button, wiring) = await PlayButton(
            new InvalidOperationException("the tool must not run"),
            loadoutId => new SynchronizerPreflightException("Can't merge regulation.bin: no vanilla to compare against.") { Action = new OpenHealthCheck(loadoutId) });

        await Play(button);

        var dialog = wiring.Presenter.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Body(dialog).Should().Contain("Can't merge regulation.bin");
        ButtonsOf(dialog).Should().Contain(ErrorPresenter.OpenHealthCheckButton);
        wiring.ErrorEvents.Should().BeEmpty();
    }

    [Fact]
    public async Task APlayThatCrashesInAGate_IsOneCrashDialogAndTheTraceOnlyInTheLog()
    {
        var (button, wiring) = await PlayButton(new InvalidOperationException("gate blew up"));

        await Play(button);

        var dialog = wiring.Presenter.Dialogs.Should().ContainSingle().Subject;
        var body = PresenterRig.Body(dialog);
        body.Should().Contain("launching the game").And.Contain("written to the log");
        body.Should().NotContain("gate blew up").And.NotContain("   at ");
        // The job's own line is a Warning because the Play button awaited it; the presenter's report is the one Error.
        var error = wiring.ErrorEvents.Should().ContainSingle().Subject;
        error.Exception.Should().BeOfType<InvalidOperationException>().Which.Message.Should().Be("gate blew up");
    }

    [Fact]
    public async Task ARefusedApply_IsOneDialogWithOpenHealthCheck()
    {
        var wiring = new Wiring(Provider);
        var loadout = await SeedLoadoutRow(GameStore.Unknown, "Apply " + Guid.NewGuid());
        var synchronizer = Substitute.For<ISynchronizerService>();
        synchronizer.Synchronize(Arg.Any<LoadoutId>()).Returns<Task>(_ => wiring.RunFailingJob(
            new SynchronizerPreflightException("Can't merge regulation.bin: no vanilla to compare against.") { Action = new OpenHealthCheck(loadout.LoadoutId) }));
        var services = wiring.With((typeof(ISynchronizerService), synchronizer));

        var applied = await OnUi(() => LoadoutApplier.Apply(services, loadout.LoadoutId));
        await Task.Delay(300);

        applied.Should().BeFalse();
        var dialog = wiring.Presenter.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The loadout was not applied");
        PresenterRig.Body(dialog).Should().Contain("Can't merge regulation.bin");
        ButtonsOf(dialog).Should().Contain(ErrorPresenter.OpenHealthCheckButton);
        wiring.ErrorEvents.Should().BeEmpty();
        wiring.Toasts.DidNotReceiveWithAnyArgs().ShowToast(default!);
    }

    [Fact]
    public async Task AnAddGameRefusedWhileCreatingTheLoadout_IsOnlyTheCannotAddDialog()
    {
        var wiring = new Wiring(Provider);
        var game = Provider.GetServices<IGameData>().OfType<IGame>().First();
        var installation = new GameInstallation(
            new GameLocatorResult
            {
                Game = game,
                Path = NexusMods.Paths.FileSystem.Shared.FromUnsanitizedFullPath("/games/refused"),
                Store = GameStore.Steam,
                StoreIdentifier = "1",
                LocatorIds = [],
                Locator = Substitute.For<IGameLocator>(),
            },
            GameLocations.Create(System.Collections.Immutable.ImmutableDictionary<LocationId, NexusMods.Paths.AbsolutePath>.Empty));

        var registry = Substitute.For<IGameRegistry>();
        registry.LocateGameInstallations().Returns([installation]);
        registry.Problems.Returns([]);

        // The job fails the way CreateLoadout's does, after some work and awaited by the page.
        var loadouts = Substitute.For<ILoadoutManager>();
        loadouts.CreateLoadout(installation, Arg.Any<string?>()).Returns(_ =>
            wiring.Jobs.Begin<CreateLoadoutJob, Loadout.ReadOnly>(new CreateLoadoutJob(installation), async _ =>
            {
                await Task.Delay(100);
                throw new SynchronizerPreflightException("Can't merge regulation.bin: no vanilla to compare against.");
            }));

        // The page watches the saved tile order, which the presenter rig's settings stand-in does not serve.
        var services = wiring.With((typeof(ILoadoutManager), loadouts), (typeof(IJobMonitor), wiring.Jobs), (typeof(ISettingsManager), Provider.GetRequiredService<ISettingsManager>()));
        var page = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<MyGamesViewModel>(services, services, registry, wiring.Presenter.Windows)));
        using var _ = await OnUi(() => Task.FromResult(page.Activator.Activate()));
        await EventuallyOnUi(() => page.InstalledGames.Should().ContainSingle());

        await OnUi(async () => await page.InstalledGames[0].AddGameCommand!.Execute());
        await Task.Delay(300);

        var dialog = wiring.Presenter.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be($"{game.DisplayName} can't be added yet");
        PresenterRig.Body(dialog).Should().Contain("Can't merge regulation.bin");
        wiring.ErrorEvents.Should().BeEmpty();
        page.InstalledGames[0].State.Should().Be(GameWidgetState.DetectedGame);
    }
}
