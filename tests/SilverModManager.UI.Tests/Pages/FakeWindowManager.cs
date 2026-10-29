using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.Windows;
using SilverModManager.App.UI.WorkspaceSystem;
using ReactiveUI;

namespace SilverModManager.UI.Tests.Pages;

internal sealed class FakeWindow : ReactiveObject, IWorkspaceWindow
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
internal sealed class FakeWindowManager : ReactiveObject, IWindowManager
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
