namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>
/// Opens <see cref="LibX11Transport"/> on <c>DISPLAY</c> as .NET saw it at process start, which is the value Avalonia's
/// <c>XOpenDisplay(NULL)</c> used to reach the same server.
/// </summary>
public sealed class LibX11TransportFactory : IXTransportFactory
{
    private readonly Func<string?> _displayName;

    public LibX11TransportFactory() : this(() => Environment.GetEnvironmentVariable("DISPLAY"))
    {
    }

    /// <param name="displayName">Where the display name comes from; asked on every <see cref="TryOpen"/>.</param>
    internal LibX11TransportFactory(Func<string?> displayName) => _displayName = displayName;

    public IXTransport? TryOpen() => LibX11Transport.TryOpen(_displayName());
}
