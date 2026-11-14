using System.Reactive.Linq;
using FluentAssertions;
using R3;
using NSubstitute;
using SilverModManager.App.UI.Pages.SteamShortcut;
using SilverModManager.App.UI.Windows;
using SilverModManager.Platform.Proton.Steam;
using SilverModManager.Platform.Proton.Steam.Shortcuts;
using SilverModManager.Sdk.Errors;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Settings;

namespace SilverModManager.UI.Tests;

public class SteamShortcutViewModelTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly SteamAccount Alice = new(65369667, "alice", "Alice");
    private static readonly SteamAccount Bob = new(338563621, "bob", "Bob");

    private sealed class Fixture
    {
        public ISteamShortcutService Service { get; } = Substitute.For<ISteamShortcutService>();
        public IProcessTracker Tracker { get; } = Substitute.For<IProcessTracker>();
        public IErrorPresenter Errors { get; } = Substitute.For<IErrorPresenter>();
        public SteamShortcutViewModel Vm { get; }
        public bool Running { get; set; }
        public int ExitRequests { get; private set; }
        public int Starts { get; private set; }

        public Fixture(SteamShortcutStatus status, bool steamRunning, bool gameMode)
        {
            Running = steamRunning;
            Tracker.IsSteamRunning().Returns(_ => Running);
            var exitRequested = false;
            var closeAndWrite = new SteamCloseAndWrite(
                Tracker.IsSteamRunning,
                () => false,
                () => { ExitRequests++; exitRequested = true; },
                () => { Starts++; Running = true; },
                (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (exitRequested) Running = false;
                    return Task.CompletedTask;
                },
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
            Vm = new SteamShortcutViewModel(Service, Tracker, closeAndWrite, Errors, status, gameMode);
        }
    }

    private static SteamShortcutStatus Status(SteamShortcutState state, params SteamAccount[] candidates)
        => new(state, state is SteamShortcutState.InSteam ? Alice : null, state is SteamShortcutState.InSteam ? 2147483650u : null, candidates);

    [Fact]
    public void TheRowTextFollowsTheServiceStatus()
    {
        var service = Substitute.For<ISteamShortcutService>();
        var source = new SteamShortcutActionSource(service, _ => Task.CompletedTask);

        void Check(SteamShortcutStatus status, string text, string? button)
        {
            service.Status().Returns(status);
            source.Current.Should().Be(new ActionButtonState(text, button));
        }

        Check(Status(SteamShortcutState.NotInSteam), "Not in Steam", "Add");
        Check(Status(SteamShortcutState.InSteam), "In Steam as SilverModManager (account Alice)", "Remove");
        Check(Status(SteamShortcutState.AddedUnconfirmed), "Added through Steam, not yet confirmed", "Remove");
        Check(Status(SteamShortcutState.AccountUndecided, Alice, Bob), "More than one Steam account; pick one", "Add");
        Check(Status(SteamShortcutState.NoSteam), "No native Steam install", null);
    }

    [Fact]
    public void AStatusThatCannotBeReadIsShownWithoutAButton()
    {
        var service = Substitute.For<ISteamShortcutService>();
        service.Status().Returns(_ => throw new SteamShortcutRefusedException("Steam's shortcuts.vdf could not be parsed.", removing: false));

        new SteamShortcutActionSource(service, _ => Task.CompletedTask).Current.Should().Be(new ActionButtonState("Steam's shortcuts.vdf could not be parsed.", null));
    }

    [Fact]
    public async Task ThePressedButtonOpensTheDialogAndTheRowReReadsTheStatusAfterwards()
    {
        var service = Substitute.For<ISteamShortcutService>();
        service.Status().Returns(Status(SteamShortcutState.NotInSteam));
        var opened = 0;
        var source = new SteamShortcutActionSource(service, _ => { opened++; return Task.CompletedTask; });
        var seen = new List<ActionButtonState>();
        using var subscription = source.State.AsSystemObservable().Subscribe(seen.Add);

        service.Status().Returns(Status(SteamShortcutState.InSteam));
        await source.Invoke(CancellationToken.None);

        opened.Should().Be(1);
        seen.Select(s => s.ButtonLabel).Should().Equal("Add", "Remove");
    }

    [Fact]
    public void OnTheDesktopWithSteamRunningBothRoutesAreOffered()
    {
        var f = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: true, gameMode: false);

        f.Vm.CanCloseAndAdd.Should().BeTrue();
        f.Vm.CanAskSteam.Should().BeTrue();
        f.Vm.CanAdd.Should().BeFalse();
        f.Vm.AskSteamNote.Should().Contain("without an icon");
    }

    [Fact]
    public void WithSteamClosedTheFileRouteIsTheOnlyOne()
    {
        var f = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: false, gameMode: false);

        f.Vm.CanAdd.Should().BeTrue();
        f.Vm.CanCloseAndAdd.Should().BeFalse();
        f.Vm.CanAskSteam.Should().BeFalse();
    }

    [Fact]
    public async Task InGameModeOnlyTheUrlRouteIsOfferedAndRemoveSaysDesktopMode()
    {
        var add = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: true, gameMode: true);
        add.Vm.CanAskSteam.Should().BeTrue();
        add.Vm.CanCloseAndAdd.Should().BeFalse();
        add.Vm.CanAdd.Should().BeFalse();
        await add.Vm.CloseAndAddAsync();
        add.ExitRequests.Should().Be(0);

        var remove = new Fixture(Status(SteamShortcutState.InSteam), steamRunning: true, gameMode: true);
        remove.Vm.CanRemove.Should().BeFalse();
        remove.Vm.CanCloseAndRemove.Should().BeFalse();
        remove.Vm.Note.Should().StartWith("Remove the shortcut from Desktop Mode.");
    }

    [Fact]
    public async Task AskingSteamPassesTheUrlRoute()
    {
        var f = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: true, gameMode: false);

        await f.Vm.AskSteamCommand.Execute();

        await f.Service.Received(1).AddAsync("steam-url", null, Arg.Any<CancellationToken>());
        f.Vm.Message.Should().StartWith("Asked Steam to add it.");
    }

    [Fact]
    public async Task AnAmbiguousAccountShowsAPickerAndPassesTheChoice()
    {
        var f = new Fixture(Status(SteamShortcutState.AccountUndecided, Alice, Bob), steamRunning: false, gameMode: false);
        f.Vm.Accounts.Select(a => a.PersonaName).Should().Equal("Alice", "Bob");
        (await f.Vm.AddCommand.CanExecute.FirstAsync()).Should().BeFalse("no account is chosen yet");

        f.Vm.SelectedAccount = Bob;
        (await f.Vm.AddCommand.CanExecute.FirstAsync()).Should().BeTrue();
        await f.Vm.AddCommand.Execute();

        await f.Service.Received(1).AddAsync("file", 338563621u, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARefusalIsShownThroughTheErrorPresenter()
    {
        var f = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: false, gameMode: false);
        var refusal = new SteamShortcutRefusedException("No native Steam install was found.", removing: false);
        f.Service.AddAsync("file", null, Arg.Any<CancellationToken>()).Returns<Task>(_ => throw refusal);

        await f.Vm.AddCommand.Execute();

        await f.Errors.Received(1).Report(refusal, "adding the app to Steam");
        f.Vm.Message.Should().Be("No native Steam install was found.");
        refusal.Action.Should().BeNull("only a refusal because Steam runs offers to close it");
    }

    [Fact]
    public async Task ASteamRunningRefusalGetsTheCloseSteamAndRetryAction()
    {
        // The poll was a moment behind: Steam started after the button was enabled.
        var f = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: false, gameMode: false);
        var refusal = new SteamShortcutRefusedException("Steam is running.", removing: false) { SteamRunning = true };
        var attempts = 0;
        f.Service.AddAsync("file", null, Arg.Any<CancellationToken>()).Returns<Task>(_ => ++attempts == 1 ? throw refusal : Task.CompletedTask);

        await f.Vm.AddCommand.Execute();

        await f.Errors.Received(1).Report(refusal, "adding the app to Steam");
        var action = refusal.Action.Should().BeOfType<CloseSteamAndRetry>().Subject;

        f.Running = true;
        await action.Retry();

        f.ExitRequests.Should().Be(1);
        f.Starts.Should().Be(1);
        attempts.Should().Be(2);
    }

    [Fact]
    public async Task CloseAndAddWritesWithSteamClosedThenStartsSteamAgain()
    {
        var f = new Fixture(Status(SteamShortcutState.NotInSteam), steamRunning: true, gameMode: false);
        var runningDuringWrite = true;
        f.Service.AddAsync("file", null, Arg.Any<CancellationToken>()).Returns(_ => { runningDuringWrite = f.Running; return Task.CompletedTask; });

        await f.Vm.CloseAndAddCommand.Execute();

        runningDuringWrite.Should().BeFalse();
        f.ExitRequests.Should().Be(1);
        f.Starts.Should().Be(1);
        f.Vm.Message.Should().Contain("Added");
    }

    [Fact]
    public async Task RemoveWithSteamClosedCallsTheService()
    {
        var f = new Fixture(Status(SteamShortcutState.InSteam), steamRunning: false, gameMode: false);
        f.Vm.Title.Should().Be("Remove from Steam");

        await f.Vm.RemoveCommand.Execute();

        await f.Service.Received(1).RemoveAsync(null, Arg.Any<CancellationToken>());
    }
}
