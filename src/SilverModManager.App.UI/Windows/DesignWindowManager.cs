using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using SilverModManager.App.UI.Dialog;
using SilverModManager.App.UI.Dialog.Enums;
using SilverModManager.App.UI.WorkspaceSystem;

namespace SilverModManager.App.UI.Windows;

public class DesignWindowManager : IWindowManager
{
    public ReadOnlyObservableCollection<WindowId> AllWindowIds { get; } = ReadOnlyObservableCollection<WindowId>.Empty;
    private IWorkspaceWindow? _activeWindow;

    public IWorkspaceWindow ActiveWindow
    {
        get => _activeWindow ?? throw new InvalidOperationException("DesignWindowManager has no active window; assign ActiveWindow first");
        set => _activeWindow = value;
    }

    public bool TryGetWindow(WindowId windowId, [NotNullWhen(true)] out IWorkspaceWindow? window)
    {
        window = null;
        return false;
    }

    public void RegisterWindow(IWorkspaceWindow window) { }

    public void UnregisterWindow(IWorkspaceWindow window) { }

    public void SaveWindowState(IWorkspaceWindow window) { }
    public bool RestoreWindowState(IWorkspaceWindow window) => false;
    
    public Task<StandardDialogResult> ShowDialog(IDialog dialog, DialogWindowType windowType)
    {
        throw new NotImplementedException();
    }
}
