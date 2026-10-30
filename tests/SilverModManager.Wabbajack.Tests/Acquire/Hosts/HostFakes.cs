using System.Net;
using System.Text;

namespace SilverModManager.Wabbajack.Tests.Acquire.Hosts;

/// <summary>One request a <see cref="FakeHost"/> saw, with its body already read.</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// An HTTP handler that answers from a function, so a test serves recorded or invented responses and no download
/// leaves the process. Counts how many requests are in flight at once for the throttle tests.
/// </summary>
internal sealed class FakeHost(Func<SeenRequest, int, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<SeenRequest> _seen = [];
    private int _inFlight;

    public FakeHost(Func<SeenRequest, int, HttpResponseMessage> respond) : this((request, earlier) => Task.FromResult(respond(request, earlier)))
    {
    }

    public IReadOnlyList<SeenRequest> Seen
    {
        get { lock (_lock) return _seen.ToArray(); }
    }

    public int MaxInFlight { get; private set; }

    public int Count(Func<SeenRequest, bool> where) => Seen.Count(where);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new SeenRequest(request.Method, request.RequestUri!, body, headers);
        int earlier;
        lock (_lock)
        {
            earlier = _seen.Count(s => s.Uri.GetLeftPart(UriPartial.Path) == seen.Uri.GetLeftPart(UriPartial.Path));
            _seen.Add(seen);
            MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
        }
        try
        {
            return await respond(seen, earlier);
        }
        finally
        {
            lock (_lock) _inFlight--;
        }
    }

    public static HttpResponseMessage Bytes(byte[] bytes, string contentType = "application/octet-stream", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return response;
    }

    public static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
        return response;
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
