using System.Net;
using System.Net.Http.Headers;

namespace SilverModManager.Networking.Thunderstore.Tests.Fixtures;

/// <summary>
/// Answers requests from a fixed URL table and records what was asked, so tests never touch the network.
/// </summary>
internal sealed class RoutingHandler : HttpMessageHandler
{
    /// <summary>
    /// One canned response. <paramref name="FinalUri"/> stands in for the redirect target: the response's
    /// <c>RequestMessage.RequestUri</c> is rewritten to it, which is what <see cref="SocketsHttpHandler"/> leaves
    /// behind after following a redirect.
    /// </summary>
    public sealed record Route(HttpStatusCode Status, byte[] Body, string ContentType, Uri? FinalUri = null);

    private readonly Dictionary<Uri, Route> _routes = new();
    public List<Uri> Requests { get; } = [];

    public RoutingHandler Add(string url, string json, HttpStatusCode status = HttpStatusCode.OK)
        => Add(url, new Route(status, System.Text.Encoding.UTF8.GetBytes(json), "application/json"));

    public RoutingHandler Add(string url, byte[] body, string contentType, Uri? finalUri = null, HttpStatusCode status = HttpStatusCode.OK)
        => Add(url, new Route(status, body, contentType, finalUri));

    public RoutingHandler Add(string url, Route route)
    {
        _routes[new Uri(url)] = route;
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(uri);

        if (!_routes.TryGetValue(uri, out var route))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });

        var content = new ByteArrayContent(route.Body);
        content.Headers.ContentType = new MediaTypeHeaderValue(route.ContentType);

        var response = new HttpResponseMessage(route.Status) { Content = content, RequestMessage = request };
        if (route.FinalUri is not null) response.RequestMessage = new HttpRequestMessage(request.Method, route.FinalUri);
        return Task.FromResult(response);
    }
}
