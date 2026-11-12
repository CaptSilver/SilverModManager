using System.Reactive.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.App.UI.Pages.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.UI.Tests.Pages;

/// <summary>
/// The Health Check button that clears what a game's diagnostics read from disk, such as old crash logs. The page
/// learns what can be cleared from the diagnostic manager, so it never knows which game it is.
/// </summary>
public class DiagnosticListClearTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0xBEEF));

    // A run publishes a new array; `[]` is the shared empty array, which the page would not see as a change.
    private readonly System.Reactive.Subjects.BehaviorSubject<Diagnostic[]> _diagnostics = new([]);
    private Func<DiagnosticClearAction[]> _clearActions = () => [];

    private async Task<(DiagnosticListViewModel Vm, IDiagnosticManager Manager)> Create(params DiagnosticClearAction[] actions)
    {
        var manager = Substitute.For<IDiagnosticManager>();
        manager.GetLoadoutDiagnostics(Arg.Any<LoadoutId>()).Returns(_diagnostics);
        manager.RefreshAsync(Arg.Any<LoadoutId>()).Returns(Task.CompletedTask);
        // One callback reading a field: stubbing the member again would run the callback that is there now.
        _clearActions = () => actions;
        manager.GetClearActions(Loadout).Returns(_ => _clearActions());

        // The view model binds on construction, which has to happen on the UI thread.
        var vm = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<DiagnosticListViewModel>(Provider, manager, new FakeWindowManager())));
        await OnUi(() =>
        {
            vm.LoadoutId = Loadout;
            vm.Activator.Activate();
            return Task.CompletedTask;
        });
        return (vm, manager);
    }

    [Fact]
    public async Task WithSomethingToClearTheButtonShowsWithTheSourcesLabel()
    {
        var (vm, _) = await Create(new DiagnosticClearAction("Clear crash reports", _ => Task.FromResult("done")));

        await EventuallyOnUi(() =>
        {
            vm.CanClear.Should().BeTrue();
            vm.ClearLabel.Should().Be("Clear crash reports");
        });
    }

    [Fact]
    public async Task TheButtonGoesAwayWhenARefreshFindsNothingLeftToClear()
    {
        var (vm, manager) = await Create(new DiagnosticClearAction("Clear crash reports", _ => Task.FromResult("done")));
        await EventuallyOnUi(() => vm.CanClear.Should().BeTrue());

        // The logs were moved outside the app; the next run's result is what notices.
        _clearActions = () => [];
        _diagnostics.OnNext(new Diagnostic[0]);

        await EventuallyOnUi(() => vm.CanClear.Should().BeFalse());
    }

    [Fact]
    public async Task ClearingRunsTheActionShowsWhatItDidAndRefreshes()
    {
        var cleared = 0;
        var (vm, manager) = await Create(new DiagnosticClearAction("Clear crash reports", _ =>
        {
            Interlocked.Increment(ref cleared);
            return Task.FromResult("Moved 18 crash logs to crash-logs-old.");
        }));
        await EventuallyOnUi(() => vm.CanClear.Should().BeTrue());
        _clearActions = () => [];
        manager.ClearReceivedCalls();

        await OnUi(async () => await vm.ClearCommand.Execute(System.Reactive.Unit.Default));

        Volatile.Read(ref cleared).Should().Be(1);
        vm.ClearMessage.Should().Be("Moved 18 crash logs to crash-logs-old.");
        await Eventually(() => manager.Received(1).RefreshAsync(Loadout));
        await EventuallyOnUi(() => vm.CanClear.Should().BeFalse());
    }

    [Fact]
    public async Task ALookStartedBeforeTheClickCannotBringTheButtonBack()
    {
        var action = new DiagnosticClearAction("Clear crash reports", _ => Task.FromResult("done"));
        var (vm, manager) = await Create(action);
        await EventuallyOnUi(() => vm.CanClear.Should().BeTrue());

        // A diagnostics publish starts a look that is still reading the folder when the user clicks.
        var lookStarted = new ManualResetEventSlim();
        var releaseLook = new ManualResetEventSlim();
        _clearActions = () =>
        {
            lookStarted.Set();
            releaseLook.Wait(TimeSpan.FromSeconds(30));
            return [action];
        };
        _diagnostics.OnNext(new Diagnostic[0]);
        lookStarted.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue();

        _clearActions = () => [];
        await OnUi(async () => await vm.ClearCommand.Execute(System.Reactive.Unit.Default));
        await EventuallyOnUi(() => vm.CanClear.Should().BeFalse());

        releaseLook.Set();
        await Task.Delay(500);
        await OnUi(() =>
        {
            vm.CanClear.Should().BeFalse("the stale look finished after the clear and must not win");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task AFailedClearIsShownOnThePage()
    {
        var (vm, _) = await Create(new DiagnosticClearAction("Clear crash reports",
            _ => Task.FromException<string>(new UnauthorizedAccessException("Access to the path is denied."))));
        await EventuallyOnUi(() => vm.CanClear.Should().BeTrue());

        await OnUi(async () => await vm.ClearCommand.Execute(System.Reactive.Unit.Default));

        vm.ClearMessage.Should().Contain("Access to the path is denied.");
    }
}
