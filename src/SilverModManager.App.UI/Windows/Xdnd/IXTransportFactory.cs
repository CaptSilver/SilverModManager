namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>Where the receiver gets its transport. The seam that lets tests script "no display".</summary>
public interface IXTransportFactory
{
    /// <returns>A transport on a connection of our own, or null when there is no display to open.</returns>
    IXTransport? TryOpen();
}
