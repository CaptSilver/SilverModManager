using Avalonia.Controls;
using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>An IX11WindowHandles that reports the XID a test set for every window, or none.</summary>
public sealed class ScriptedX11WindowHandles : IX11WindowHandles
{
    public nint? Xid { get; set; }

    public nint? XidOf(TopLevel window) => Xid;
}
