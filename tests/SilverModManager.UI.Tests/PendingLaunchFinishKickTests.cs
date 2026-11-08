using System.Collections.Immutable;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NexusMods.Paths;
using NSubstitute;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;

namespace SilverModManager.UI.Tests;

public class PendingLaunchFinishKickTests
{
    private readonly IPendingLaunchFinisher _finisher = Substitute.For<IPendingLaunchFinisher>();
    private readonly ILaunchFinishMarkers _markers = Substitute.For<ILaunchFinishMarkers>();
    private readonly IGameRegistry _registry = Substitute.For<IGameRegistry>();
    private readonly IWindowNotificationService _toasts = Substitute.For<IWindowNotificationService>();
    private readonly ActiveLaunchSessions _sessions = new();
    private readonly Subject<LaunchFinishReport> _reports = new();
    private readonly GameInstallation _marked = Installation("1");
    private readonly GameInstallation _unmarked = Installation("2");

    public PendingLaunchFinishKickTests()
    {
        _registry.LocateGameInstallations().Returns([_marked, _unmarked]);
        _finisher.Reports.Returns(_reports);
        _finisher.FinishIfPending(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns(call => new LaunchFinishReport(LaunchFinishOutcome.Finished, call.Arg<GameInstallation>()));
    }

    private static GameInstallation Installation(string storeId)
    {
        var game = Substitute.For<IGameData>();
        game.DisplayName.Returns("Test Game " + storeId);
        var folder = FileSystem.Shared.FromUnsanitizedFullPath("/tmp/pending-finish-kick-" + storeId);
        var result = new GameLocatorResult
        {
            Game = game,
            Path = folder,
            Store = GameStore.Steam,
            StoreIdentifier = storeId,
            LocatorIds = ImmutableArray<LocatorId>.Empty,
            Locator = Substitute.For<IGameLocator>(),
            Platform = OSPlatform.Windows,
        };
        return new GameInstallation(result, GameLocations.Create(ImmutableDictionary<LocationId, AbsolutePath>.Empty.Add(LocationId.Game, folder)));
    }

    private static PendingLaunchFinish Marker() => new(LoadoutId.From(1), "Loadout", Guid.NewGuid(), ["Gate"], PendingLaunchFinishState.Pending);

    private PendingLaunchFinishKick NewKick() => new(_finisher, _markers, _registry, _sessions, _toasts, NullLogger<PendingLaunchFinishKick>.Instance);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time");
            await Task.Delay(10);
        }
    }

    private int FinishCalls(GameInstallation installation)
        => _finisher.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IPendingLaunchFinisher.FinishIfPending) && ReferenceEquals(c.GetArguments()[0], installation));

    [Fact(Timeout = 30000)]
    public async Task Start_FinishesEveryInstallationWithAMarker_AndNotTheOthers()
    {
        _markers.Read(_marked).Returns(Marker());
        using var kick = NewKick();

        kick.Start();

        await Eventually(() => FinishCalls(_marked) == 1);
        await Task.Delay(100);
        FinishCalls(_unmarked).Should().Be(0);
    }

    [Fact(Timeout = 30000)]
    public async Task Start_Twice_SubscribesOnce()
    {
        _markers.Read(_marked).Returns(Marker());
        using var kick = NewKick();

        kick.Start();
        kick.Start();
        await Eventually(() => FinishCalls(_marked) >= 1);
        await Task.Delay(100);

        FinishCalls(_marked).Should().Be(1);
        _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.Finished, _marked, "Done."));
        await Eventually(() => _toasts.ReceivedCalls().Any());
        await Task.Delay(100);
        _toasts.ReceivedCalls().Should().HaveCount(1);
    }

    [Fact(Timeout = 30000)]
    public async Task AReport_DoesNotWaitForTheToastToBeShown()
    {
        using var gate = new ManualResetEventSlim();
        _toasts.When(t => t.ShowToast(Arg.Any<string>(), Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>()))
            .Do(_ => gate.Wait(TimeSpan.FromSeconds(20)));
        using var kick = NewKick();
        kick.Start();

        var publish = Task.Run(() => _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.Finished, _marked, "Done.")));

        var finished = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(5)));
        gate.Set();
        finished.Should().BeSameAs(publish, "publishing a report must not wait on the UI thread showing its toast");
    }

    [Fact(Timeout = 30000)]
    public async Task AToastThatThrows_DoesNotFailThePublisher()
    {
        _toasts.When(t => t.ShowToast(Arg.Any<string>(), Arg.Any<ToastNotificationVariant>(), Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>()))
            .Do(_ => throw new InvalidOperationException("no window"));
        using var kick = NewKick();
        kick.Start();

        var act = () => _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.Finished, _marked, "Done."));

        act.Should().NotThrow();
        await Eventually(() => _toasts.ReceivedCalls().Any());
    }

    [Fact(Timeout = 30000)]
    public async Task AReport_IsToastedWithItsMessage_AndSilentOnesAreNot()
    {
        using var kick = NewKick();
        kick.Start();

        _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.Finished, _marked, "Test Game closed before its changes were taken back into 'Loadout'. That is done now."));
        _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.GameRunning, _marked, "Test Game is still running."));
        _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.ClearedLoadoutGone, _marked, "The loadout has been deleted."));
        _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.SkippedHeld, _marked));
        _reports.OnNext(new LaunchFinishReport(LaunchFinishOutcome.Finished, _marked));

        await Eventually(() => _toasts.ReceivedCalls().Count() >= 3);
        _toasts.Received(1).ShowToast(MarkdownText.Inline("Test Game closed before its changes were taken back into 'Loadout'. That is done now."), ToastNotificationVariant.Success, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        _toasts.Received(1).ShowToast(MarkdownText.Inline("Test Game is still running."), ToastNotificationVariant.Failure, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        _toasts.Received(1).ShowToast(MarkdownText.Inline("The loadout has been deleted."), ToastNotificationVariant.Neutral, Arg.Any<TimeSpan?>(), Arg.Any<DialogButtonDefinition[]?>(), Arg.Any<Action<ButtonDefinitionId>?>());
        _toasts.ReceivedCalls().Should().HaveCount(3);
    }

    [Fact(Timeout = 30000)]
    public async Task WhenALaunchEnds_WithAMarkerStillPending_TheInstallationIsKickedAgain()
    {
        using var kick = NewKick();
        kick.Start();
        await Task.Delay(100);
        FinishCalls(_marked).Should().Be(0);

        var registration = _sessions.Register(_marked);
        _markers.Read(_marked).Returns(Marker());
        registration.Dispose();

        await Eventually(() => FinishCalls(_marked) == 1);
    }

    [Fact(Timeout = 30000)]
    public async Task ALaunchThatEndsDuringTheFirstCheck_IsKickedAgain()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reads = 0;
        _markers.Read(_marked).Returns(_ =>
        {
            if (Interlocked.Increment(ref reads) != 1) return Marker();
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(20));
            return null;
        });
        using var kick = NewKick();
        kick.Start();
        entered.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue();

        _sessions.Register(_marked).Dispose();
        release.Set();

        await Eventually(() => FinishCalls(_marked) == 1);
    }

    [Fact(Timeout = 30000)]
    public async Task WhenALaunchEnds_WithNoMarker_NothingIsKicked()
    {
        using var kick = NewKick();
        kick.Start();

        _sessions.Register(_marked).Dispose();
        await Task.Delay(200);

        FinishCalls(_marked).Should().Be(0);
    }
}
