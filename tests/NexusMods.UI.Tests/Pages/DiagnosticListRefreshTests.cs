using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics;
using NexusMods.App.UI.Dialog;
using NexusMods.App.UI.Dialog.Enums;
using NexusMods.App.UI.Pages.Diagnostics;
using NexusMods.App.UI.Windows;
using NexusMods.App.UI.WorkspaceSystem;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Loadouts;
using NSubstitute;
using ReactiveUI;

namespace NexusMods.UI.Tests.Pages;

public class DiagnosticListRefreshTests(IServiceProvider provider) : AUiTest(provider)
{
    private static readonly LoadoutId Loadout = LoadoutId.From(EntityId.From(0xBEEF));

    private sealed record Setup(DiagnosticListViewModel Vm, IDiagnosticManager Manager, FakeWindowManager Windows);

    private sealed class FakeWindow : ReactiveObject, IWorkspaceWindow
    {
        private bool _isActive = true;
        public bool IsActive { get => _isActive; set => this.RaiseAndSetIfChanged(ref _isActive, value); }
        public WindowId WindowId => throw new NotSupportedException();
        public IWorkspaceController WorkspaceController => throw new NotSupportedException();
        public ReactiveCommand<System.Reactive.Unit, bool> BringWindowToFront => throw new NotSupportedException();
    }

    /// <summary>
    /// Holds one window, like the real window manager does with a single window open.
    /// </summary>
    private sealed class FakeWindowManager : ReactiveObject, IWindowManager
    {
        public FakeWindow Window { get; } = new();
        public IWorkspaceWindow ActiveWindow { get => Window; set => throw new NotSupportedException(); }
        public ReadOnlyObservableCollection<WindowId> AllWindowIds { get; } = new(new ObservableCollection<WindowId>());

        public bool TryGetWindow(WindowId windowId, [NotNullWhen(true)] out IWorkspaceWindow? window) => throw new NotSupportedException();
        public void RegisterWindow(IWorkspaceWindow window) => throw new NotSupportedException();
        public void UnregisterWindow(IWorkspaceWindow window) => throw new NotSupportedException();
        public void SaveWindowState(IWorkspaceWindow window) => throw new NotSupportedException();
        public bool RestoreWindowState(IWorkspaceWindow window) => throw new NotSupportedException();
        public Task<StandardDialogResult> ShowDialog(IDialog dialog, DialogWindowType windowType) => throw new NotSupportedException();
    }

    private async Task<Setup> Create()
    {
        var manager = Substitute.For<IDiagnosticManager>();
        manager.GetLoadoutDiagnostics(Arg.Any<LoadoutId>()).Returns(Observable.Never<Diagnostic[]>());
        manager.RefreshAsync(Arg.Any<LoadoutId>()).Returns(Task.CompletedTask);

        // Window focus reaches the page as an ActiveWindow change on the window manager.
        var windows = new FakeWindowManager();

        // The view model binds on construction, which has to happen on the UI thread.
        var vm = await OnUi(() => Task.FromResult(ActivatorUtilities.CreateInstance<DiagnosticListViewModel>(Provider, manager, windows)));
        vm.LoadoutId = Loadout;
        return new Setup(vm, manager, windows);
    }

    [Fact]
    public async Task Test_RefreshCommand_RefreshesTheLoadoutAndReportsBusyWhileItRuns()
    {
        var (vm, manager, _) = await Create();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.RefreshAsync(Loadout).Returns(running.Task);

        var execution = await OnUi(() => Task.FromResult(vm.RefreshCommand.Execute().ToTask()));

        await EventuallyOnUi(() => vm.IsRefreshing.Should().BeTrue());
        await manager.Received(1).RefreshAsync(Loadout);

        running.SetResult();
        await execution;
        await EventuallyOnUi(() => vm.IsRefreshing.Should().BeFalse());
    }

    [Fact]
    public async Task Test_OpeningThePage_RefreshesTheLoadout()
    {
        var (vm, manager, _) = await Create();

        await OnUi(() =>
        {
            vm.Activator.Activate();
            return Task.CompletedTask;
        });

        await Eventually(() => manager.Received(1).RefreshAsync(Loadout));
    }

    [Fact]
    public async Task Test_ActivatingTheWindow_RefreshesTheLoadoutOnceForABurstOfActivations()
    {
        var (vm, manager, windows) = await Create();
        await OnUi(() =>
        {
            vm.Activator.Activate();
            return Task.CompletedTask;
        });
        await Eventually(() => manager.Received(1).RefreshAsync(Loadout));

        await OnUi(() =>
        {
            for (var i = 0; i < 3; i++)
            {
                windows.Window.IsActive = false;
                windows.Window.IsActive = true;
            }
            return Task.CompletedTask;
        });

        await Eventually(() => manager.Received(2).RefreshAsync(Loadout));
        await Task.Delay(1500);
        await manager.Received(2).RefreshAsync(Loadout);
    }
}
