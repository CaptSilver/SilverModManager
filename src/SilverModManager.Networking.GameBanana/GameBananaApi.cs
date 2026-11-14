using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Networking.GameBanana.DTOs;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Networking.GameBanana;

internal sealed class GameBananaApi(HttpClient http, HostThrottle throttle, TimeProvider time, GameBananaSettings settings) : IGameBananaApi
{
    private const string Root = "https://gamebanana.com/apiv11";

    // The two-transfer cap is per host; every request of this client goes to the same one.
    private const string Host = "gamebanana.com";

    private readonly Dictionary<Uri, (DateTimeOffset At, GameBananaListing Listing)> _listings = [];
    private readonly Lock _gate = new();

    public Task<GameBananaListing> Subfeed(int gameId, int page, int perPage, CancellationToken ct)
        => Listing(new Uri($"{Root}/Game/{gameId}/Subfeed?_nPage={page}&_nPerpage={perPage}"), ct);

    public Task<GameBananaListing> Index(int gameId, GameBananaSection section, string sort, int page, int perPage, CancellationToken ct)
        => Listing(new Uri($"{Root}/{section}/Index?_nPage={page}&_nPerpage={perPage}&_aFilters[Generic_Game]={gameId}&_sSort={Uri.EscapeDataString(sort)}"), ct);

    public Task<GameBananaListing> Search(int gameId, string text, int page, CancellationToken ct)
        => Listing(new Uri($"{Root}/Util/Search/Results?_sModelName=Mod&_sOrder=best_match&_idGameRow={gameId}&_sSearchString={Uri.EscapeDataString(text)}&_nPage={page}"), ct);

    public async Task<GameBananaProfile> Profile(long modId, CancellationToken ct)
    {
        var uri = new Uri($"{Root}/Mod/{modId}/ProfilePage");
        using var response = await Send(uri, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) throw new GameBananaUnavailableException(modId);
        var profile = await Read<GameBananaProfile>(response, uri, ct);

        // A stub has a name and these flags and nothing to install; a page with files is a real one whatever it says.
        if (profile.IsPrivate || profile.IsWithheld) throw new GameBananaUnavailableException(modId, profile.Name);
        return profile;
    }

    private async Task<GameBananaListing> Listing(Uri uri, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_listings.TryGetValue(uri, out var kept) && time.GetUtcNow() - kept.At < settings.RefreshAge) return kept.Listing;
        }

        using var response = await Send(uri, ct);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"GameBanana request {uri} failed with {(int)response.StatusCode} {response.StatusCode}", inner: null, response.StatusCode);

        var reply = await Read<ListingReply>(response, uri, ct);
        var listing = new GameBananaListing(reply.Records ?? [], reply.Metadata?.RecordCount ?? 0, reply.Metadata?.IsComplete ?? true);
        lock (_gate) _listings[uri] = (time.GetUtcNow(), listing);
        return listing;
    }

    private async Task<HttpResponseMessage> Send(Uri uri, CancellationToken ct)
    {
        using var slot = await throttle.Enter(Host, ct);
        var response = await http.GetAsync(uri, ct);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;

        var wait = WaitOf(response);
        response.Dispose();
        throw new GameBananaRateLimitedException(wait);
    }

    private TimeSpan WaitOf(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        return header?.Date is { } date && date > time.GetUtcNow() ? date - time.GetUtcNow() : TimeSpan.Zero;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, Uri uri, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Options, ct) ?? throw new JsonException("The reply is empty.");
        }
        catch (JsonException e)
        {
            throw new GameBananaReplyUnreadableException(uri, e);
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private sealed record ListingReply(
        [property: JsonPropertyName("_aMetadata")] ListingMetadata? Metadata,
        [property: JsonPropertyName("_aRecords")] IReadOnlyList<GameBananaRecord>? Records);

    private sealed record ListingMetadata(
        [property: JsonPropertyName("_nRecordCount")] int RecordCount,
        [property: JsonPropertyName("_bIsComplete")] bool IsComplete);
}
