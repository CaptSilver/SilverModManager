using SilverModManager.App.UI.Windows.Xdnd;

namespace SilverModManager.UI.Tests.Windows.Xdnd;

/// <summary>An IXTransportFactory that counts how often it was asked and hands out the transport a test set, or none.</summary>
public sealed class FakeXTransportFactory : IXTransportFactory
{
    public int Opened { get; private set; }

    /// <summary>What TryOpen returns; null scripts "no display".</summary>
    public IXTransport? Transport { get; set; }

    public IXTransport? TryOpen()
    {
        Opened++;
        return Transport;
    }
}
