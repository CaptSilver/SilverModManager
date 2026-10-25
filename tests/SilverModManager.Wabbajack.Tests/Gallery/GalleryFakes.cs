using System.Net;
using System.Text;

namespace SilverModManager.Wabbajack.Tests.Gallery;

internal static class GalleryFixtureFiles
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Gallery", "Fixtures", name));

    public static readonly Uri Repositories = new("https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/repositories.json");
    public static readonly Uri Official = new("https://raw.githubusercontent.com/wabbajack-tools/mod-lists/master/reports/modlists.json");
    public static readonly Uri Community = new("https://raw.githubusercontent.com/someone/community-lists/main/modlists.json");
    public static readonly Uri ServerError = new("https://raw.githubusercontent.com/broken/server-error/main/modlists.json");
    public static readonly Uri Junk = new("https://raw.githubusercontent.com/broken/junk/main/modlists.json");
}

/// <summary>Serves canned bodies by address, records every request and how many were in flight at once.</summary>
internal sealed class GalleryHandler : HttpMessageHandler
{
    private readonly object _lock = new();
    private readonly List<Uri> _requests = [];
    private int _current;
    private int _peak;

    public Dictionary<Uri, (HttpStatusCode Status, string Body)> Responses { get; } = new();

    /// <summary>When set, every request throws as if the machine had no network.</summary>
    public bool Offline { get; set; }

    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    public int Peak => Volatile.Read(ref _peak);

    public IReadOnlyList<Uri> Requests
    {
        get { lock (_lock) return _requests.ToArray(); }
    }

    public static GalleryHandler WithFixtures()
    {
        var handler = new GalleryHandler();
        handler.Responses[GalleryFixtureFiles.Repositories] = (HttpStatusCode.OK, GalleryFixtureFiles.Read("repositories.json"));
        handler.Responses[GalleryFixtureFiles.Official] = (HttpStatusCode.OK, GalleryFixtureFiles.Read("modlists-official.json"));
        handler.Responses[GalleryFixtureFiles.Community] = (HttpStatusCode.OK, GalleryFixtureFiles.Read("modlists-community.json"));
        handler.Responses[GalleryFixtureFiles.ServerError] = (HttpStatusCode.InternalServerError, "boom");
        handler.Responses[GalleryFixtureFiles.Junk] = (HttpStatusCode.OK, GalleryFixtureFiles.Read("modlists-junk.json"));
        return handler;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock) _requests.Add(request.RequestUri!);
        var now = Interlocked.Increment(ref _current);
        int seen;
        while (now > (seen = Volatile.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, now, seen) != seen) { }
        try
        {
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            if (Offline) throw new HttpRequestException("no network");
            if (!Responses.TryGetValue(request.RequestUri!, out var response))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body, Encoding.UTF8, "application/json") };
        }
        finally
        {
            Interlocked.Decrement(ref _current);
        }
    }
}
