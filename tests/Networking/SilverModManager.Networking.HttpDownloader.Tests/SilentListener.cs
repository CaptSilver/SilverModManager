using System.Net;
using System.Net.Sockets;

namespace SilverModManager.Networking.HttpDownloader.Tests;

/// <summary>
/// Accepts connections and then says nothing, which is what a host behind a dropped route looks like once connected.
/// </summary>
public sealed class SilentListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly List<TcpClient> _held = [];

    public SilentListener()
    {
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    /// <summary>
    /// Number of connections accepted so far, so a test can wait until its request is actually hanging.
    /// </summary>
    public int Accepted { get { lock (_held) return _held.Count; } }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>
    /// A URI on this listener with a query that stands in for a signed download link.
    /// </summary>
    public Uri UriWithSignature(string signature) => new($"http://127.0.0.1:{Port}/file?signature={signature}");

    private async Task AcceptLoop()
    {
        try
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                lock (_held) _held.Add(client);
            }
        }
        catch (Exception)
        {
            // Listener stopped
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        lock (_held)
        {
            foreach (var client in _held) client.Dispose();
        }
    }
}
