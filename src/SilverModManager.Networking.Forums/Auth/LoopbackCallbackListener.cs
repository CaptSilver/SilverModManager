using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// The authorization code a forum sent back to the loopback redirect.
/// </summary>
public sealed record CallbackResult(string Code)
{
    // The code is a credential; keep it out of a log through the record's generated ToString.
    public override string ToString() => "CallbackResult { Code = <redacted> }";
}

/// <summary>
/// Catches the browser's redirect at the end of a forum sign-in: a listener on 127.0.0.1 and an ephemeral
/// port that serves <c>GET /callback</c> until one request carries the expected <c>state</c> and a code or an
/// error.
/// </summary>
/// <remarks>
/// It is a bare <see cref="TcpListener"/> with a request-line and <c>Host</c> parse rather than
/// <c>HttpListener</c>, which on Linux wants its prefix before it binds. Anything that is not the real
/// callback (a favicon fetch, a wrong or missing <c>state</c>, a malformed request) gets an error status and
/// the wait goes on, so a stray request cannot use up the one slot. The query string carries the code and the
/// state, so it is never logged.
/// </remarks>
public sealed class LoopbackCallbackListener : IDisposable
{
    private const string CallbackPath = "/callback";
    private const int MaxRequestBytes = 8192;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly ILogger _logger;
    private readonly TimeSpan _requestTimeout;
    private int _waiting;
    private int _closed;

    /// <param name="logger">Receives what the listener did, never what the requests carried.</param>
    /// <param name="requestTimeout">
    /// How long one connection may take to send its request line and headers before it is dropped, so a
    /// silent connection cannot hold up the real callback behind it.
    /// </param>
    public LoopbackCallbackListener(ILogger? logger = null, TimeSpan? requestTimeout = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        RedirectUri = new Uri($"http://127.0.0.1:{port}{CallbackPath}");
    }

    /// <summary>
    /// The address to hand the forum as <c>redirect_uri</c>.
    /// </summary>
    public Uri RedirectUri { get; }

    /// <summary>
    /// Waits for the callback and closes the socket when it ends, however it ends.
    /// </summary>
    /// <param name="state">The anti-forgery value the callback must carry.</param>
    /// <param name="siteName">The forum's display name, for the page and the failure texts.</param>
    /// <exception cref="SignInDeclinedException">The callback carried an error, e.g. the user pressed Deny.</exception>
    /// <exception cref="SignInTimedOutException">No matching callback arrived within <paramref name="timeout"/>.</exception>
    public async Task<CallbackResult> WaitAsync(string state, string siteName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _waiting, 1) != 0)
            throw new InvalidOperationException("The listener is already waiting for a callback.");

        using var timeoutSource = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(linked.Token);
                if (await TryServe(client, state, siteName, linked.Token) is { } result) return result;
            }
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SignInTimedOutException(siteName);
        }
        catch (Exception e) when (Volatile.Read(ref _closed) == 1 && e is SocketException or ObjectDisposedException)
        {
            // Closing the listener aborts the pending accept. Ending as cancelled rather than faulted means a wait
            // that its owner has already given up on is not reported as an unobserved task exception.
            throw new OperationCanceledException("The sign-in listener was closed.", e);
        }
        finally
        {
            _listener.Stop();
        }
    }

    public void Dispose()
    {
        Volatile.Write(ref _closed, 1);
        _listener.Stop();
    }

    /// <summary>
    /// Answers one connection. Returns the result when it was the real callback, null to keep waiting.
    /// </summary>
    private async Task<CallbackResult?> TryServe(TcpClient client, string state, string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var stream = client.GetStream();
            var head = await ReadHead(stream, cancellationToken);
            if (head is null) return null;

            var (status, body, outcome) = Route(head, state, siteName);
            await WriteResponse(stream, status, body, cancellationToken);
            _logger.LogDebug("The sign-in listener answered {Status} to a request for {Route}", status, outcome.Route);

            if (outcome.Declined) throw new SignInDeclinedException(siteName);
            return outcome.Code is null ? null : new CallbackResult(outcome.Code);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // A browser that hung up mid-request is not the callback.
            _logger.LogDebug("A sign-in listener connection ended early ({Kind})", e.GetType().Name);
            return null;
        }
    }

    private readonly record struct Outcome(string Route, string? Code = null, bool Declined = false);

    private (int Status, string Body, Outcome Outcome) Route(string head, string state, string siteName)
    {
        var lines = head.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
            return (400, "Bad request.", new Outcome("malformed"));
        if (requestLine[0] != "GET" || !HasLoopbackHost(lines))
            return (400, "Bad request.", new Outcome("refused"));

        var target = requestLine[1];
        var queryStart = target.IndexOf('?');
        var path = queryStart < 0 ? target : target[..queryStart];
        if (path != CallbackPath) return (404, "Not found.", new Outcome("other"));

        var query = HttpUtility.ParseQueryString(queryStart < 0 ? "" : target[(queryStart + 1)..]);
        if (!StateMatches(query["state"], state)) return (400, "Bad request.", new Outcome("callback with the wrong state"));

        var name = WebUtility.HtmlEncode(siteName);
        if (!string.IsNullOrEmpty(query["error"]))
            return (200, Page($"Sign-in to {name} was declined. You can close this tab."), new Outcome("callback", Declined: true));

        var code = query["code"];
        if (string.IsNullOrEmpty(code)) return (400, "Bad request.", new Outcome("callback without a code"));
        return (200, Page($"Signed in to {name}. You can close this tab and return to SilverModManager."), new Outcome("callback", code));
    }

    // A page that names another host in Host is not talking to the address the redirect was built from.
    private bool HasLoopbackHost(string[] lines)
    {
        var expected = $"127.0.0.1:{RedirectUri.Port}";
        foreach (var line in lines.Skip(1))
        {
            if (!line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)) continue;
            return line["Host:".Length..].Trim() == expected;
        }

        return false;
    }

    private static bool StateMatches(string? received, string expected)
    {
        if (string.IsNullOrEmpty(received)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(received), Encoding.UTF8.GetBytes(expected));
    }

    private static string Page(string message)
        => $"<!doctype html><html><head><meta charset=\"utf-8\"><title>SilverModManager</title></head><body><p>{message}</p></body></html>";

    /// <summary>
    /// Reads up to the blank line that ends the headers. Null for a connection that sent nothing usable in time.
    /// </summary>
    private async Task<string?> ReadHead(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(_requestTimeout);

        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        try
        {
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), requestTimeout.Token);
                if (read == 0) break;
                length += read;
                var text = Encoding.Latin1.GetString(buffer, 0, length);
                var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (end >= 0) return text[..end];
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        // Ran out of room or the peer hung up before the headers ended: answer it as malformed if it sent anything.
        return length == 0 ? null : Encoding.Latin1.GetString(buffer, 0, length);
    }

    private static async Task WriteResponse(NetworkStream stream, int status, string body, CancellationToken cancellationToken)
    {
        var reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Bad Request" };
        var payload = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\n" +
                   "Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
