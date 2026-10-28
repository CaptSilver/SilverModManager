using System.Net;

namespace SilverModManager.Networking.HttpDownloader.Tests;

/// <summary>
/// Serves one small body at any path. <see cref="LocalHttpServer"/> builds a 512 MB buffer on construction,
/// which is far too heavy for a test that only needs a few bytes.
/// </summary>
public sealed class SingleFileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly byte[] _body;

    public SingleFileServer(byte[] body)
    {
        _body = body;
        while (true)
        {
            var port = Random.Shared.Next(49152, 65535);
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                _listener.Start();
                Uri = new Uri($"http://127.0.0.1:{port}/file?signature=SECRET");
                break;
            }
            catch (HttpListenerException)
            {
                // Port taken, try another
            }
        }

        _ = Task.Run(ServeLoop);
    }

    public Uri Uri { get; }

    private async Task ServeLoop()
    {
        try
        {
            while (_listener.IsListening)
            {
                var context = await _listener.GetContextAsync();
                using var response = context.Response;
                response.StatusCode = 200;
                response.ContentLength64 = _body.Length;
                if (context.Request.HttpMethod != "HEAD")
                    await response.OutputStream.WriteAsync(_body);
            }
        }
        catch (Exception)
        {
            // Listener stopped
        }
    }

    public void Dispose() => _listener.Stop();
}
