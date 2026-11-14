using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using R3;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Overlays.Generic.MessageBox.Ok;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Windows;
using SilverModManager.Platform.Proton.Launch;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.UI.Sdk;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.UI.Sdk.Dialog.Enums;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Health Check action for a game left running after an unfinished launch: it re-checks who holds the game,
/// asks once, stops the game, finishes the launch, and refreshes, in that order.
/// </summary>
public class StopGameAndFinishActionHandlerTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly TrackedProcess Game = new(777, "Game.exe");
    private static readonly TrackedProcess Helper = new(778, "GameHelper.exe");
    private static readonly TrackedProcess Tool = new(600, "xEdit");

    private readonly List<string> _events = [];
    private readonly List<IDialog> _dialogs = [];
    private readonly List<MessageBoxOkViewModel> _messages = [];
    private readonly IGameLaunchSession _session = Substitute.For<IGameLaunchSession>();
    private readonly IRunningGameDetector _detector = Substitute.For<IRunningGameDetector>();
    private readonly IGameProcessFactory _processes = Substitute.For<IGameProcessFactory>();
    private readonly IPendingLaunchFinisher _finisher = Substitute.For<IPendingLaunchFinisher>();
    private readonly IDiagnosticManager _diagnostics = Substitute.For<IDiagnosticManager>();
    private TrackedProcess? _survivor;

    private StopGameAndFinishActionHandler Build(ButtonDefinitionId? answer = null, RunningProcesses? running = null)
    {
        _detector.Running(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>()).Returns(running ?? new RunningProcesses([Game, Helper], [Tool]));
        _processes.ShutDownGameAsync(Arg.Any<GameInstallation>(), Arg.Any<IReadOnlySet<int>?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _events.Add("shutdown"); return Task.FromResult(_survivor); });
        _finisher.FinishIfPending(Arg.Any<GameInstallation>(), Arg.Any<CancellationToken>())
            .Returns(call => { _events.Add("finish"); return Task.FromResult(new LaunchFinishReport(LaunchFinishOutcome.Finished, call.Arg<GameInstallation>())); });
        _diagnostics.RefreshAsync(Arg.Any<LoadoutId>()).Returns(_ => { _events.Add("refresh"); return Task.CompletedTask; });

        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(call =>
        {
            _dialogs.Add(call.Arg<IDialog>());
            return Task.FromResult(new StandardDialogResult { ButtonId = answer ?? ButtonDefinitionId.Accept });
        });

        var overlays = Substitute.For<IOverlayController>();
        overlays.EnqueueAndWait(Arg.Do<IOverlayViewModel<Unit>>(vm => _messages.Add((MessageBoxOkViewModel)vm))).Returns(Task.FromResult<Unit>(default));

        var services = new ServiceCollection()
            .AddSingleton(Connection)
            .AddSingleton(_session)
            .AddSingleton(_detector)
            .AddSingleton(_processes)
            .AddSingleton(_finisher)
            .AddSingleton(overlays)
            .BuildServiceProvider();
        return new StopGameAndFinishActionHandler(services, windows, _diagnostics);
    }

    private static string DialogText(IDialog dialog)
        => ((SilverModManager.App.UI.Dialog.Dialog)dialog).ViewModel.ContentViewModel.Should().BeAssignableTo<IDialogStandardContentViewModel>().Subject.Text;

    [Fact]
    public async Task Execute_RefusesWhenASessionAppearedSinceTheDiagnosticWasShown()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Stop Refuse");
        _session.Describe(Arg.Any<GameInstallation>()).Returns(new HeldByOtherProcess("Another SilverModManager is running the game.", 99));
        var handler = Build();

        await handler.ExecuteAsync(loadout.LoadoutId, []);

        _messages.Should().ContainSingle().Which.Should().Match<MessageBoxOkViewModel>(m => m.Title == "Cannot stop the game" && m.Description == "Another SilverModManager is running the game.");
        _dialogs.Should().BeEmpty();
        _events.Should().Equal("refresh");
    }

    [Fact]
    public async Task Execute_AsksOnceNamingEveryGameProcessAndLeavesToolsOut()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Stop Ask");
        var handler = Build();

        await handler.ExecuteAsync(loadout.LoadoutId, []);

        var text = DialogText(_dialogs.Should().ContainSingle().Subject);
        text.Should().Contain("These processes will be stopped:").And.Contain("Game.exe (pid 777)").And.Contain("GameHelper.exe (pid 778)");
        text.Should().Contain("Left alone:").And.Contain("xEdit (pid 600)");
        text.IndexOf("xEdit", StringComparison.Ordinal).Should().BeGreaterThan(text.IndexOf("Left alone:", StringComparison.Ordinal));
        text.IndexOf("Game.exe", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("Left alone:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_Declined_SignalsNothing()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Stop Decline");
        var handler = Build(ButtonDefinitionId.Cancel);

        await handler.ExecuteAsync(loadout.LoadoutId, []);

        _dialogs.Should().ContainSingle();
        _events.Should().Equal("refresh");
    }

    [Fact]
    public async Task Execute_ShutsDownThenFinishesThenRefreshes_InThatOrder()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Stop Order");
        var handler = Build();

        await handler.ExecuteAsync(loadout.LoadoutId, []);

        _events.Should().Equal("shutdown", "finish", "refresh");
    }

    [Fact]
    public async Task Execute_ASurvivorIsReportedAndTheFinishIsSkipped()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Stop Survivor");
        _survivor = new TrackedProcess(4242, "wineserver");
        var handler = Build();

        await handler.ExecuteAsync(loadout.LoadoutId, []);

        _messages.Should().ContainSingle().Which.Should().Match<MessageBoxOkViewModel>(m => m.Title == "The game did not stop" && m.Description == "wineserver (pid 4242) survived SIGKILL.");
        _events.Should().Equal("shutdown", "refresh");
    }

    [Fact]
    public async Task Execute_WithNothingRunning_GoesStraightToTheFinish()
    {
        var loadout = await SeedLoadoutRow(GameStore.Steam, "Stop Nothing");
        var handler = Build(running: new RunningProcesses([], [Tool]));

        await handler.ExecuteAsync(loadout.LoadoutId, []);

        _dialogs.Should().BeEmpty();
        _events.Should().Equal("finish", "refresh");
    }
}
