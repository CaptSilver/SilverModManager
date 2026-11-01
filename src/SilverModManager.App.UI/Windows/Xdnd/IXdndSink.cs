using Avalonia.Input;
using Avalonia;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>What the window does with a drag; each call returns the effect it will perform (None refuses). Points are window pixels.</summary>
public interface IXdndSink
{
    DragDropEffects Enter(PixelPoint at, IDataObject data, DragDropEffects allowed);
    DragDropEffects Over(PixelPoint at, IDataObject data, DragDropEffects allowed);
    void Leave();
    DragDropEffects Drop(PixelPoint at, IDataObject data, DragDropEffects allowed);
}
