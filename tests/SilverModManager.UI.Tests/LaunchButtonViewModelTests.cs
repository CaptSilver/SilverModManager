using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.App.UI;
using SilverModManager.App.UI.LeftMenu.Items;
using SilverModManager.App.UI.Resources;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Jobs;
using SilverModManager.Sdk.Loadouts;
using NSubstitute;
using SilverModManager.UI.Tests.Helpers;
using ReactiveUI;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests;

public class LaunchButtonViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    /// <summary>
    /// A run tool whose run lasts until its token is cancelled, standing in for a launch that waits on Steam.
    /// </summary>
    private sealed class Rig
    {
        public readonly BehaviorSubject<bool> Waiting = new(false);
        public readonly BehaviorSubject<bool> InstallStep = new(false);
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken ToolToken;
        public string GameName = "";
        public GameInstallation Installation = null!;
        public LaunchButtonViewModel ViewModel = null!;
    }

    private async Task<Rig> Build(PresenterRig? presenter = null, Func<Exception>? failure = null, bool withoutRunTool = false, TimeProvider? clock = null, ActiveLaunchSessions? sessions = null)
    {
        var rig = new Rig();
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Launch button");

        rig.GameName = loadout.InstallationInstance.Game.DisplayName;
        rig.Installation = loadout.InstallationInstance;
        var tool = Substitute.For<IRunGameTool>();
        tool.WaitingForSteam.Returns(rig.Waiting);
        tool.RunningInstallStep.Returns(rig.InstallStep);

        var toolManager = Substitute.For<IToolManager>();
        toolManager.GetTools(Arg.Any<Loadout.ReadOnly>()).Returns(withoutRunTool ? [] : [tool]);
        toolManager.RunTool(Arg.Any<ITool>(), Arg.Any<Loadout.ReadOnly>(), Arg.Any<IJobMonitor>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                if (failure is not null) throw failure();
                rig.ToolToken = call.Arg<CancellationToken>();
                rig.Started.TrySetResult();
                try { await rig.Finish.Task.WaitAsync(rig.ToolToken); }
                catch (OperationCanceledException) { }
                return call.Arg<Loadout.ReadOnly>();
            });

        var monitor = Provider.GetRequiredService<IJobMonitor>();
        rig.ViewModel = new LaunchButtonViewModel(
            NullLogger<ILaunchButtonViewModel>.Instance,
            toolManager,
            Connection,
            monitor,
            presenter?.Services ?? Provider,
            Provider.GetRequiredService<GameRunningTracker>(),
            clock,
            sessions)
        {
            LoadoutId = loadout.LoadoutId,
        };
        return rig;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition never held");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task WhileTheToolWaitsForSteam_TheLabelSaysSoAndCancelIsAvailable()
    {
        var rig = await Build();
        var run = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_RUNNING);
        rig.ViewModel.IsWaitingForSteam.Should().BeFalse();

        rig.Waiting.OnNext(true);

        await Eventually(() => rig.ViewModel.IsWaitingForSteam);
        rig.ViewModel.Label.Should().Be(string.Format(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM, rig.GameName));
        var canCancel = false;
        using (rig.ViewModel.CancelCommand.CanExecute.Subscribe(value => canCancel = value))
            await Eventually(() => canCancel);

        rig.Waiting.OnNext(false);
        await Eventually(() => !rig.ViewModel.IsWaitingForSteam);
        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_RUNNING);

        rig.Finish.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AfterTenSecondsOfWaiting_TheLabelAddsAHintThatSteamMayBeWaitingOnAWindow()
    {
        var clock = new FakeTimeProvider();
        var rig = await Build(clock: clock);
        var run = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);
        var plain = string.Format(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM, rig.GameName);

        clock.Advance(TimeSpan.FromSeconds(9.5));
        rig.ViewModel.Label.Should().Be(plain, "the hint is for a wait that drags on");

        clock.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => rig.ViewModel.Label != plain);
        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM_SLOW);
        rig.ViewModel.IsWaitingForSteam.Should().BeTrue("Cancel stays available next to the hint");

        rig.Finish.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhileSteamRunsTheInstallStep_TheLabelSaysSoAtOnce_ThenGoesBackToWaiting()
    {
        var rig = await Build(clock: new FakeTimeProvider());
        var run = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);

        rig.InstallStep.OnNext(true);
        var installing = string.Format(Language.LaunchButtonViewModel_LaunchGame_STEAM_INSTALL_STEP, rig.GameName);
        await Eventually(() => rig.ViewModel.Label == installing);

        rig.InstallStep.OnNext(false);
        await Eventually(() => rig.ViewModel.Label != installing);
        rig.ViewModel.Label.Should().Be(string.Format(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM, rig.GameName));
        rig.Finish.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task AGameThatShowsUpBeforeTenSeconds_NeverGetsTheHint()
    {
        var clock = new FakeTimeProvider();
        var rig = await Build(clock: clock);
        var run = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);

        clock.Advance(TimeSpan.FromSeconds(5));
        rig.Waiting.OnNext(false);
        await Eventually(() => !rig.ViewModel.IsWaitingForSteam);
        clock.Advance(TimeSpan.FromSeconds(30));

        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_RUNNING);
        rig.Finish.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task ASecondWait_StartsItsOwnTenSeconds()
    {
        var clock = new FakeTimeProvider();
        var rig = await Build(clock: clock);
        var run = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);
        clock.Advance(TimeSpan.FromSeconds(8));
        rig.Waiting.OnNext(false);
        await Eventually(() => !rig.ViewModel.IsWaitingForSteam);

        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);
        clock.Advance(TimeSpan.FromSeconds(8));

        rig.ViewModel.Label.Should().Be(string.Format(Language.LaunchButtonViewModel_LaunchGame_WAITING_FOR_STEAM, rig.GameName));
        rig.Finish.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Label_IsRunningWhileAForwardedSessionHoldsTheInstallation()
    {
        // A launch Steam started through the app's wrapper is a registered session with no run-game job behind it.
        var sessions = new ActiveLaunchSessions(null);
        var rig = await Build(sessions: sessions);
        using var activation = await OnUi(() => Task.FromResult(rig.ViewModel.Activator.Activate()));
        await Eventually(() => rig.ViewModel.Label == Language.LaunchButtonViewModel_LaunchGame_LAUNCH);

        var registration = sessions.Register(rig.Installation);
        await Eventually(() => rig.ViewModel.Label == Language.LaunchButtonViewModel_LaunchGame_RUNNING);
        (await rig.ViewModel.IsRunningObservable.FirstAsync()).Should().BeTrue("the button is busy for as long as the label says Running");

        registration.Dispose();
        await Eventually(() => rig.ViewModel.Label == Language.LaunchButtonViewModel_LaunchGame_LAUNCH);
        (await rig.ViewModel.IsRunningObservable.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_EndsTheRunAtOnceAndTheButtonReturnsToPlay()
    {
        var rig = await Build();
        var run = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);

        await rig.ViewModel.CancelCommand.Execute();

        rig.ToolToken.IsCancellationRequested.Should().BeTrue("cancel has to reach the tool's token");
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_LAUNCH);
        rig.ViewModel.IsWaitingForSteam.Should().BeFalse();
    }

    [Fact]
    public async Task Cancel_IsNotAvailableUntilTheToolIsWaiting()
    {
        var rig = await Build();

        (await rig.ViewModel.CancelCommand.CanExecute.FirstAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task ALaunchAfterACancelIsNotCancelled()
    {
        var rig = await Build();
        var first = rig.ViewModel.Command.Execute().ToTask();
        await rig.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.Waiting.OnNext(true);
        await Eventually(() => rig.ViewModel.IsWaitingForSteam);
        await rig.ViewModel.CancelCommand.Execute();
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        var firstToken = rig.ToolToken;
        var second = rig.ViewModel.Command.Execute().ToTask();
        await Eventually(() => rig.ToolToken != firstToken);

        rig.ToolToken.IsCancellationRequested.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        rig.Finish.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class RefusedToStart(string message) : UserFacingException(message)
    {
        public override string Title => "The game was not started";
    }

    [Fact]
    public async Task ARefusalFromTheLaunch_IsOnePlainDialogAndNoErrorEvent()
    {
        var presenter = new PresenterRig(Provider);
        var rig = await Build(presenter, () => new RefusedToStart("Steam is updating the game."));

        await rig.ViewModel.Command.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(10));

        var dialog = presenter.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The game was not started");
        PresenterRig.Body(dialog).Should().Contain("Steam is updating the game.");
        presenter.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_LAUNCH);
    }

    [Fact]
    public async Task AGameWithNoRunTool_IsOnePlainRefusalNamingTheGame()
    {
        var presenter = new PresenterRig(Provider);
        var rig = await Build(presenter, withoutRunTool: true);

        await rig.ViewModel.Command.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(10));

        var dialog = presenter.Dialogs.Should().ContainSingle().Subject;
        dialog.WindowTitle.Should().Be("The game was not started");
        PresenterRig.Body(dialog).Should().Contain(rig.GameName).And.Contain("no way to start");
        presenter.Log.Entries.Should().NotContain(e => e.Level >= LogLevel.Error);
        rig.ViewModel.Label.Should().Be(Language.LaunchButtonViewModel_LaunchGame_LAUNCH);
    }

    [Fact]
    public async Task ACrashDuringTheLaunch_IsOneCrashDialog()
    {
        var presenter = new PresenterRig(Provider);
        var rig = await Build(presenter, () => new InvalidOperationException("gate blew up"));

        await rig.ViewModel.Command.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(10));

        var dialog = presenter.Dialogs.Should().ContainSingle().Subject;
        PresenterRig.Body(dialog).Should().Contain("launching the game").And.Contain("written to the log");
        PresenterRig.Body(dialog).Should().NotContain("gate blew up");
        presenter.Log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error);
    }
}
