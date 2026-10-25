using System.Collections.Immutable;
using System.Reactive.Linq;
using Avalonia.Threading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Diagnostics.References;
using SilverModManager.Abstractions.Games;
using SilverModManager.App.UI.Dialog;
using SilverModManager.UI.Sdk.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.UI.Sdk.Dialog.Enums;
using SilverModManager.App.UI.Controls.MarkdownRenderer;
using SilverModManager.App.UI.Overlays;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.App.UI.Pages.SteamLaunchOption;
using SilverModManager.App.UI.Windows;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Games.FromSoft.Cleanup;
using SilverModManager.Platform.Proton.Doctor;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using NSubstitute;
using Loadout = SilverModManager.Sdk.Loadouts.Loadout;

namespace SilverModManager.UI.Tests.Pages;

public class DiagnosticActionTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId SomeLoadout = LoadoutId.From(EntityId.From(0xCAFE));

    private sealed class RecordingHandler(string actionId) : IDiagnosticActionHandler
    {
        public string ActionId { get; } = actionId;
        public List<(LoadoutId Loadout, ImmutableArray<string> Arguments)> Calls { get; } = [];

        public Task ExecuteAsync(LoadoutId loadoutId, ImmutableArray<string> arguments)
        {
            Calls.Add((loadoutId, arguments));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<DiagnosticDetailsViewModel>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static Diagnostic DiagnosticWith(params DiagnosticAction[] actions) => new()
    {
        Id = new DiagnosticId("SilverModManager.UI.Tests", 1),
        Title = "Title",
        Severity = DiagnosticSeverity.Suggestion,
        Summary = DiagnosticMessage.From("Summary"),
        Details = DiagnosticMessage.From("Details"),
        DataReferences = new Dictionary<DataReferenceDescription, IDataReference>(),
        Actions = [..actions],
    };

    private Task<DiagnosticDetailsViewModel> DetailsFor(Diagnostic diagnostic, IEnumerable<IDiagnosticActionHandler> handlers, ILogger<DiagnosticDetailsViewModel> logger)
        => OnUi(() => Task.FromResult(new DiagnosticDetailsViewModel(
            Substitute.For<IWindowManager>(),
            Provider.GetRequiredService<IDiagnosticWriter>(),
            Provider.GetRequiredService<IMarkdownRendererViewModel>(),
            diagnostic,
            SomeLoadout,
            handlers,
            logger)));

    [Fact]
    public async Task Test_ADiagnosticWithoutActionsHasNoButtons()
    {
        var vm = await DetailsFor(DiagnosticWith(), [new RecordingHandler("fix")], new RecordingLogger());

        vm.Actions.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_AnActionWithAHandlerGetsAButtonThatRunsItForTheLoadout()
    {
        var handler = new RecordingHandler("fix");
        var vm = await DetailsFor(DiagnosticWith(new DiagnosticAction("fix", "Fix it", ["a", "b"])), [handler], new RecordingLogger());

        var button = vm.Actions.Should().ContainSingle().Subject;
        button.Label.Should().Be("Fix it");

        await OnUi(async () => await button.Command.Execute());

        var call = handler.Calls.Should().ContainSingle().Subject;
        call.Loadout.Should().Be(SomeLoadout);
        call.Arguments.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Test_AnActionNobodyHandlesShowsNoButtonAndIsLogged()
    {
        var logger = new RecordingLogger();
        var vm = await DetailsFor(
            DiagnosticWith(new DiagnosticAction("unknown", "Mystery", []), new DiagnosticAction("fix", "Fix it", [])),
            [new RecordingHandler("fix")],
            logger);

        vm.Actions.Select(a => a.Label).Should().Equal("Fix it");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Which.Message.Should().Contain("unknown");
    }

    [Fact]
    public async Task Test_AFailingHandlerIsLoggedAndTheButtonStaysUsable()
    {
        var logger = new RecordingLogger();
        var handler = Substitute.For<IDiagnosticActionHandler>();
        handler.ActionId.Returns("fix");
        handler.ExecuteAsync(Arg.Any<LoadoutId>(), Arg.Any<ImmutableArray<string>>()).Returns(Task.FromException(new InvalidOperationException("boom")));
        var vm = await DetailsFor(DiagnosticWith(new DiagnosticAction("fix", "Fix it", [])), [handler], logger);

        await OnUi(async () => await vm.Actions.Single().Command.Execute());

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error && e.Message.Contains("fix"));
    }

    [Fact]
    public async Task Test_TheSteamActionOpensTheLaunchOptionDialogForTheLoadoutAndRefreshesAfterItCloses()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix From Health Check");
        var events = new List<string>();
        var closeDialog = new TaskCompletionSource<System.Reactive.Unit>(TaskCreationOptions.RunContinuationsAsynchronously);

        var overlays = Substitute.For<IOverlayController>();
        overlays.EnqueueAndWait(Arg.Any<IOverlayViewModel<System.Reactive.Unit>>()).Returns(call =>
        {
            events.Add("opened " + ((SteamLaunchOptionViewModel)call.Arg<IOverlayViewModel<System.Reactive.Unit>>()).LoadoutName);
            return closeDialog.Task;
        });
        var manager = Substitute.For<IDiagnosticManager>();
        manager.RefreshAsync(Arg.Any<LoadoutId>()).Returns(_ =>
        {
            events.Add("refreshed");
            return Task.CompletedTask;
        });
        var handler = new UseLoadoutFromSteamActionHandler(new OverlayProvider(Provider, overlays), manager);

        handler.ActionId.Should().Be(DiagnosticActions.UseLoadoutFromSteamId);
        var running = handler.ExecuteAsync(seeded.LoadoutId, []);

        await Task.Delay(200);
        events.Should().Equal("opened Fix From Health Check");
        await manager.DidNotReceive().RefreshAsync(Arg.Any<LoadoutId>());

        closeDialog.SetResult(System.Reactive.Unit.Default);
        await running.WaitAsync(TimeSpan.FromSeconds(30));

        events.Should().Equal("opened Fix From Health Check", "refreshed");
        await manager.Received(1).RefreshAsync(seeded.LoadoutId);
    }

    #region restore folder from backup

    private sealed class RestoreFixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("restore-action").FullName;
        public string Link { get; }
        public string Backup { get; }

        public RestoreFixture()
        {
            Link = Path.Combine(Root, "Game");
            Backup = Path.Combine(Root, "Game.backup-1");
            Directory.CreateSymbolicLink(Link, Path.Combine(Root, "gone"));
            Directory.CreateDirectory(Backup);
            File.WriteAllText(Path.Combine(Backup, "a.dat"), "precious");
        }

        public bool LinkIsStillDangling => new DirectoryInfo(Link).LinkTarget is not null;
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class ServicesWith(IServiceProvider inner, params (Type Type, object Instance)[] overrides) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => overrides.FirstOrDefault(o => o.Type == serviceType).Instance ?? inner.GetService(serviceType);
    }

    private static IWindowManager Answering(ButtonDefinitionId button)
    {
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(new StandardDialogResult { ButtonId = button });
        return windows;
    }

    private RestoreFolderFromBackupActionHandler RestoreHandler(IWindowManager windows, IDiagnosticManager manager, bool gameRunning = false)
    {
        var session = Substitute.For<IGameLaunchSession>();
        session.IsRunning(Arg.Any<GameInstallation>()).Returns(gameRunning);
        return RestoreHandler(windows, manager, session);
    }

    private RestoreFolderFromBackupActionHandler RestoreHandler(IWindowManager windows, IDiagnosticManager manager, IGameLaunchSession session)
    {
        var services = new ServicesWith(Provider, (typeof(IGameLaunchSession), session), (typeof(IOverlayController), Substitute.For<IOverlayController>()));
        return new RestoreFolderFromBackupActionHandler(services, windows, manager);
    }

    [Fact]
    public async Task Test_RestoreAsksFirst_ThenMovesTheBackupIntoPlaceAndRefreshes()
    {
        using var fixture = new RestoreFixture();
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Restore");
        var manager = Substitute.For<IDiagnosticManager>();
        var windows = Answering(ButtonDefinitionId.Accept);
        var handler = RestoreHandler(windows, manager);

        handler.ActionId.Should().Be(DiagnosticActions.RestoreFolderFromBackupId);
        await handler.ExecuteAsync(seeded.LoadoutId, [fixture.Link, fixture.Backup]).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        fixture.LinkIsStillDangling.Should().BeFalse();
        File.ReadAllText(Path.Combine(fixture.Link, "a.dat")).Should().Be("precious");
        await manager.Received(1).RefreshAsync(seeded.LoadoutId);
    }

    [Fact]
    public async Task Test_RestoreDoesNothingWhenTheUserDeclines()
    {
        using var fixture = new RestoreFixture();
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Restore");
        var handler = RestoreHandler(Answering(ButtonDefinitionId.Cancel), Substitute.For<IDiagnosticManager>());

        await handler.ExecuteAsync(seeded.LoadoutId, [fixture.Link, fixture.Backup]).WaitAsync(TimeSpan.FromSeconds(30));

        fixture.LinkIsStillDangling.Should().BeTrue();
        File.Exists(Path.Combine(fixture.Backup, "a.dat")).Should().BeTrue();
    }

    [Fact]
    public async Task Test_RestoreRefusesWhileTheGameRunsWithoutAsking()
    {
        using var fixture = new RestoreFixture();
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Restore");
        var windows = Answering(ButtonDefinitionId.Accept);
        var handler = RestoreHandler(windows, Substitute.For<IDiagnosticManager>(), gameRunning: true);

        await handler.ExecuteAsync(seeded.LoadoutId, [fixture.Link, fixture.Backup]).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        fixture.LinkIsStillDangling.Should().BeTrue();
        File.Exists(Path.Combine(fixture.Backup, "a.dat")).Should().BeTrue();
    }

    [Fact]
    public async Task Test_RestoreLeavesEverythingAloneWhenThePathChangedWhileTheDialogWasOpen()
    {
        using var fixture = new RestoreFixture();
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Restore");
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(_ =>
        {
            // The link is replaced by a real folder while the user reads the question.
            File.Delete(fixture.Link);
            Directory.CreateDirectory(fixture.Link);
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };
        });
        var handler = RestoreHandler(windows, Substitute.For<IDiagnosticManager>());

        await handler.ExecuteAsync(seeded.LoadoutId, [fixture.Link, fixture.Backup]).WaitAsync(TimeSpan.FromSeconds(30));

        Directory.GetFileSystemEntries(fixture.Link).Should().BeEmpty();
        File.ReadAllText(Path.Combine(fixture.Backup, "a.dat")).Should().Be("precious");
    }

    [Fact]
    public async Task Test_RestoreRefusesWhenTheGameStartedWhileTheDialogWasOpen()
    {
        using var fixture = new RestoreFixture();
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Restore");
        var session = Substitute.For<IGameLaunchSession>();
        session.IsRunning(Arg.Any<GameInstallation>()).Returns(false, true);
        var handler = RestoreHandler(Answering(ButtonDefinitionId.Accept), Substitute.For<IDiagnosticManager>(), session);

        await handler.ExecuteAsync(seeded.LoadoutId, [fixture.Link, fixture.Backup]).WaitAsync(TimeSpan.FromSeconds(30));

        fixture.LinkIsStillDangling.Should().BeTrue();
        File.ReadAllText(Path.Combine(fixture.Backup, "a.dat")).Should().Be("precious");
    }

    #endregion

    private sealed class OverlayProvider(IServiceProvider inner, IOverlayController overlays) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOverlayController) ? overlays : inner.GetService(serviceType);
    }

    [Fact]
    public async Task Test_ShowCleanupStepsOpensADialogWithTheGuidesStepsAndRunsNothing()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Cleanup");
        var guide = Substitute.For<IGameCleanupGuide>();
        guide.AppliesTo(Arg.Any<GameInstallation>()).Returns(true);
        guide.Describe(Arg.Any<Loadout.ReadOnly>()).Returns(["1. Move a over b"]);
        var windows = Answering(ButtonDefinitionId.Accept);
        var handler = new ShowCleanupStepsActionHandler(new ServicesWith(Provider, (typeof(IEnumerable<IGameCleanupGuide>), new[] { guide })), windows);

        handler.ActionId.Should().Be(GameCleanupActions.ShowStepsId);
        await handler.ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        guide.Received(1).Describe(Arg.Any<Loadout.ReadOnly>());
    }

    [Fact]
    public async Task Test_ShowCleanupStepsFailsLoudlyForAGameWithoutAGuide()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Cleanup");
        var windows = Answering(ButtonDefinitionId.Accept);
        var handler = new ShowCleanupStepsActionHandler(new ServicesWith(Provider, (typeof(IEnumerable<IGameCleanupGuide>), Array.Empty<IGameCleanupGuide>())), windows);

        var act = () => handler.ExecuteAsync(seeded.LoadoutId, []);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no cleanup steps*");
        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
    }

    #region Fix cleanup

    private static CleanupPlan PlanOf(params string[] deletes)
        => new([..deletes.Select(d => new CleanupStep(CleanupStepKind.Delete, $"Delete {d}", d))]);

    private static CleanupFixOutcome Applied(params string[] deletes)
        => new(CleanupFixStatus.Applied, new CleanupResult([..PlanOf(deletes).Steps], null));

    private FixCleanupActionHandler FixHandler(IWindowManager windows, IDiagnosticManager manager, ICleanupFixService fixer)
    {
        var services = new ServicesWith(Provider, (typeof(ICleanupFixService), fixer), (typeof(IOverlayController), Substitute.For<IOverlayController>()));
        return new FixCleanupActionHandler(services, windows, manager);
    }

    private static ICleanupFixService FixerPlanning(CleanupPlan plan)
    {
        var fixer = Substitute.For<ICleanupFixService>();
        fixer.Plan(Arg.Any<Loadout.ReadOnly>()).Returns(plan);
        return fixer;
    }

    [Fact]
    public async Task Test_FixShowsTheStepsFirst_ThenRunsThemAsAJobAndRefreshesHealthCheck()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var plan = PlanOf("a", "b");
        var fixer = FixerPlanning(plan);
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>()).Returns(Applied("a", "b"));
        var manager = Substitute.For<IDiagnosticManager>();
        var windows = Answering(ButtonDefinitionId.Accept);
        var handler = FixHandler(windows, manager, fixer);

        handler.ActionId.Should().Be(GameCleanupActions.FixId);
        await handler.ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.Received(1).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        await fixer.Received(1).FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Is<CleanupPlan>(p => p.SameSteps(plan)), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>());
        await manager.Received(1).RefreshAsync(seeded.LoadoutId);
    }

    [Fact]
    public async Task Test_FixDoesNothingWhenTheConfirmationIsCancelled()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf("a"));
        var manager = Substitute.For<IDiagnosticManager>();

        await FixHandler(Answering(ButtonDefinitionId.Cancel), manager, fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await fixer.DidNotReceive().FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>());
        await manager.DidNotReceive().RefreshAsync(Arg.Any<LoadoutId>());
    }

    [Fact]
    public async Task Test_FixShowsANewPlanForConfirmationInsteadOfApplyingIt()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var confirmed = PlanOf("a");
        var changed = PlanOf("a", "b");
        var fixer = FixerPlanning(confirmed);
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Is<CleanupPlan>(p => p.SameSteps(confirmed)), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>())
            .Returns(new CleanupFixOutcome(CleanupFixStatus.PlanChanged, NewPlan: changed));
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Is<CleanupPlan>(p => p.SameSteps(changed)), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>())
            .Returns(Applied("a", "b"));
        var manager = Substitute.For<IDiagnosticManager>();
        var windows = Answering(ButtonDefinitionId.Accept);

        await FixHandler(windows, manager, fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.Received(2).ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        await fixer.Received(2).FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>());
        await manager.Received(2).RefreshAsync(seeded.LoadoutId);
    }

    [Fact]
    public async Task Test_FixStopsWhenTheNewPlanIsNotConfirmed()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf("a"));
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>())
            .Returns(new CleanupFixOutcome(CleanupFixStatus.PlanChanged, NewPlan: PlanOf("a", "b")));
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(
            new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept },
            new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel });

        await FixHandler(windows, Substitute.For<IDiagnosticManager>(), fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await fixer.Received(1).FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_FixStillRefreshesWhenThePasswordDialogWasDismissed()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf("a"));
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>())
            .Returns(new CleanupFixOutcome(CleanupFixStatus.Cancelled, Message: "dismissed"));
        var manager = Substitute.For<IDiagnosticManager>();

        await FixHandler(Answering(ButtonDefinitionId.Accept), manager, fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await manager.Received(1).RefreshAsync(seeded.LoadoutId);
    }

    [Fact]
    public async Task Test_FixHasNothingToConfirmForAFolderWithNothingToDo()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf());
        var windows = Answering(ButtonDefinitionId.Accept);

        await FixHandler(windows, Substitute.For<IDiagnosticManager>(), fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        await windows.DidNotReceive().ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>());
        await fixer.DidNotReceive().FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Any<ManualCommandPrompt>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Test_FixPassesAPromptThatReturnsTrueOnceTheFlagIsGone()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf("a"));
        ManualCommandPrompt? prompt = null;
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Do<ManualCommandPrompt>(p => prompt = p), Arg.Any<CancellationToken>()).Returns(Applied("a"));
        var windows = Answering(ButtonDefinitionId.Accept);
        await FixHandler(windows, Substitute.For<IDiagnosticManager>(), fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        (await prompt!("sudo chattr -i '/x'", null, () => true, CancellationToken.None)).Should().BeTrue();
        windows.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IWindowManager.ShowDialog)).Should().Be(1, "a cleared flag needs no dialog");

        var cleared = false;
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(_ =>
        {
            cleared = true;
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };
        });
        (await prompt("sudo chattr -i '/x'", null, () => cleared, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Test_FixPromptReturnsFalseWhenTheUserCancels()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf("a"));
        ManualCommandPrompt? prompt = null;
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Do<ManualCommandPrompt>(p => prompt = p), Arg.Any<CancellationToken>()).Returns(Applied("a"));
        var windows = Substitute.For<IWindowManager>();
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(
            new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept },
            new StandardDialogResult { ButtonId = ButtonDefinitionId.Cancel });
        await FixHandler(windows, Substitute.For<IDiagnosticManager>(), fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        (await prompt!("sudo chattr -i '/x'", "pkexec is not installed", () => false, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30))).Should().BeFalse();
    }

    [Fact]
    public async Task Test_FixPromptShowsItsDialogOnTheUiThreadWhenCalledFromTheJob()
    {
        var seeded = await SeedLoadoutRow(GameStore.Steam, "Fix");
        var fixer = FixerPlanning(PlanOf("a"));
        ManualCommandPrompt? prompt = null;
        fixer.FixAsync(Arg.Any<Loadout.ReadOnly>(), Arg.Any<CleanupPlan>(), Arg.Do<ManualCommandPrompt>(p => prompt = p), Arg.Any<CancellationToken>()).Returns(Applied("a"));
        var windows = Answering(ButtonDefinitionId.Accept);
        await FixHandler(windows, Substitute.For<IDiagnosticManager>(), fixer).ExecuteAsync(seeded.LoadoutId, []).WaitAsync(TimeSpan.FromSeconds(30));

        bool? onUiThread = null;
        var cleared = false;
        windows.ShowDialog(Arg.Any<IDialog>(), Arg.Any<DialogWindowType>()).Returns(_ =>
        {
            onUiThread = Dispatcher.UIThread.CheckAccess();
            cleared = true;
            return new StandardDialogResult { ButtonId = ButtonDefinitionId.Accept };
        });

        // The job body runs on the thread pool, and Avalonia only builds windows on the UI thread.
        (await Task.Run(() => prompt!("sudo chattr -i '/x'", "pkexec is not installed", () => cleared, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(30))).Should().BeTrue();
        onUiThread.Should().BeTrue();
    }

    #endregion
}
