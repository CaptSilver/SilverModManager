using System.Net;
using System.Net.Http.Headers;

namespace SilverModManager.Networking.ModIo.Tests.Fixtures;

/// <summary>
/// Answers by method and path and records each request with its bearer header and body, so tests never touch the network.
/// </summary>
internal sealed class RecordingHandler : HttpMessageHandler
{
    internal sealed record Seen(HttpMethod Method, Uri Uri, string? Authorization, string Body);

    private readonly Dictionary<(HttpMethod, string), (HttpStatusCode Status, Func<Uri, string> Json, TimeSpan? RetryAfter)> _routes = [];

    public List<Seen> Requests { get; } = [];

    public RecordingHandler Add(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? retryAfter = null)
        => Add(method, path, _ => json, status, retryAfter);

    /// <summary>Answers with a reply built from the request's address, which is how a listing's pages differ.</summary>
    public RecordingHandler Add(HttpMethod method, string path, Func<Uri, string> json, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? retryAfter = null)
    {
        _routes[(method, path)] = (status, json, retryAfter);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new Seen(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        if (!_routes.TryGetValue((request.Method, request.RequestUri!.AbsolutePath), out var route))
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };

        var response = new HttpResponseMessage(route.Status) { Content = new StringContent(route.Json(request.RequestUri), new MediaTypeHeaderValue("application/json")), RequestMessage = request };
        if (route.RetryAfter is { } wait) response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }
}
