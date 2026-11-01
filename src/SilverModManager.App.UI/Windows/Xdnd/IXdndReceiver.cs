using Avalonia.Controls;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Attaches the receiver to the X11 windows the app opens. One per process.</summary>
public interface IXdndReceiver
{
    /// <summary>Starts receiving on <paramref name="window"/> once it has a platform handle; stops when it closes or the
    /// returned handle is disposed. Safe to call for a window that is not X11 (no-op).</summary>
    IDisposable Attach(Window window);
}

/// <summary>The receiver for the previewer and for code that has no X11 to serve: attaches nothing.</summary>
public sealed class NullXdndReceiver : IXdndReceiver
{
    public static readonly NullXdndReceiver Instance = new();

    private NullXdndReceiver() { }

    public IDisposable Attach(Window window) => Nothing.Instance;

    private sealed class Nothing : IDisposable
    {
        public static readonly Nothing Instance = new();

        public void Dispose() { }
    }
}
