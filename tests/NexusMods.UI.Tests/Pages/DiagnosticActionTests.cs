using System.Collections.Immutable;
using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.Abstractions.Diagnostics.References;
using NexusMods.App.UI.Controls.MarkdownRenderer;
using NexusMods.App.UI.Overlays;
using NexusMods.App.UI.Pages.Diagnostics;
using NexusMods.App.UI.Pages.SteamLaunchOption;
using NexusMods.App.UI.Windows;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using Loadout = NexusMods.Sdk.Loadouts.Loadout;

namespace NexusMods.UI.Tests.Pages;

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
        Id = new DiagnosticId("NexusMods.UI.Tests", 1),
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

    private sealed class OverlayProvider(IServiceProvider inner, IOverlayController overlays) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType == typeof(IOverlayController) ? overlays : inner.GetService(serviceType);
    }
}
