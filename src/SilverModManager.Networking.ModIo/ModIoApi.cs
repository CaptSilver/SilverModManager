using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilverModManager.Abstractions.Downloads;
using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.ModIo;

internal sealed class ModIoApi(HttpClient http, ISecretStore secrets, HostThrottle throttle, TimeProvider time, ModIoSettings settings) : IModIoApi
{
    private const string Root = "https://api.mod.io/v1";

    // The two-transfer cap is per host; every request of this client goes to the same one.
    private const string Host = "api.mod.io";

    // From mod.io's error reference: the access token lacks the write scope, and the account has not agreed to the terms.
    private const int MissingWriteScope = 11003;
    private const int TermsNotAgreed = 11074;

    // The largest _limit mod.io accepts.
    private const int MaxPage = 100;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private readonly Dictionary<Uri, (DateTimeOffset At, ModIoPage<ModIoMod> Page)> _listings = [];
    private readonly Lock _gate = new();

    public async Task<ModIoPage<ModIoMod>> Mods(uint gameId, ModIoQuery query, CancellationToken ct)
    {
        var parameters = new List<string>();
        if (query.Text.Trim() is { Length: > 0 } text) parameters.Add($"_q={Uri.EscapeDataString(text)}");
        parameters.Add($"_sort={Uri.EscapeDataString(query.Sort)}");
        parameters.Add($"_limit={query.Limit.ToString(CultureInfo.InvariantCulture)}");
        parameters.Add($"_offset={query.Offset.ToString(CultureInfo.InvariantCulture)}");
        var uri = new Uri($"{Root}/games/{gameId}/mods?{string.Join('&', parameters)}");

        lock (_gate)
        {
            if (_listings.TryGetValue(uri, out var kept) && time.GetUtcNow() - kept.At < settings.RefreshAge) return kept.Page;
        }

        var token = await StoredToken(ct);
        using var response = await Send(HttpMethod.Get, uri, token, null, ct);
        await ThrowUnlessSuccess(response, ct);
        var reply = await Read<ListReply<ModIoMod>>(response, uri, ct);
        var page = new ModIoPage<ModIoMod>(reply.Data ?? [], reply.ResultTotal, reply.ResultOffset);
        lock (_gate) _listings[uri] = (time.GetUtcNow(), page);
        return page;
    }

    public async Task<ModIoMod?> Mod(uint gameId, uint modId, CancellationToken ct)
    {
        var uri = new Uri($"{Root}/games/{gameId}/mods/{modId}");
        using var response = await Send(HttpMethod.Get, uri, await StoredToken(ct), null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await ThrowUnlessSuccess(response, ct);
        return await Read<ModIoMod>(response, uri, ct);
    }

    public async Task<IReadOnlyList<ModIoFile>> Files(uint gameId, uint modId, CancellationToken ct)
    {
        return await Everything<ModIoFile>($"{Root}/games/{gameId}/mods/{modId}/files?", ct);
    }

    public async Task Subscribe(uint gameId, uint modId, bool includeDependencies, CancellationToken ct)
    {
        var uri = new Uri($"{Root}/games/{gameId}/mods/{modId}/subscribe");
        var form = new FormUrlEncodedContent([new("include_dependencies", includeDependencies ? "true" : "false")]);
        using var response = await Send(HttpMethod.Post, uri, await StoredToken(ct), form, ct);
        await ThrowUnlessSuccess(response, ct);
    }

    public async Task Unsubscribe(uint gameId, uint modId, CancellationToken ct)
    {
        var uri = new Uri($"{Root}/games/{gameId}/mods/{modId}/subscribe");
        using var response = await Send(HttpMethod.Delete, uri, await StoredToken(ct), null, ct);
        await ThrowUnlessSuccess(response, ct);
    }

    public async Task<IReadOnlyList<ModIoMod>> Subscribed(uint gameId, CancellationToken ct)
    {
        return await Everything<ModIoMod>($"{Root}/me/subscribed?game_id={gameId}&", ct);
    }

    public async Task<ModIoUser> Me(CancellationToken ct) => await Me(await StoredToken(ct), ct);

    public async Task<ModIoUser> Me(string token, CancellationToken ct)
    {
        var uri = new Uri($"{Root}/me");
        using var response = await Send(HttpMethod.Get, uri, token, null, ct);
        await ThrowUnlessSuccess(response, ct);
        return await Read<ModIoUser>(response, uri, ct);
    }

    // mod.io hands out at most 100 results a page; the callers want the whole listing, so every page is read.
    private async Task<IReadOnlyList<T>> Everything<T>(string addressWithQuery, CancellationToken ct)
    {
        var token = await StoredToken(ct);
        var all = new List<T>();
        while (true)
        {
            var uri = new Uri(string.Create(CultureInfo.InvariantCulture, $"{addressWithQuery}_limit={MaxPage}&_offset={all.Count}"));
            using var response = await Send(HttpMethod.Get, uri, token, null, ct);
            await ThrowUnlessSuccess(response, ct);
            var reply = await Read<ListReply<T>>(response, uri, ct);
            var page = reply.Data ?? [];
            all.AddRange(page);
            // An empty page ends it too, so a total that overstates the listing cannot keep this asking.
            if (page.Count == 0 || all.Count >= reply.ResultTotal) return all;
        }
    }

    private async Task<string> StoredToken(CancellationToken ct)
        => await secrets.GetAsync(ModIoSecretKeys.Token, ct) switch
        {
            SecretResult.Found { Value.Length: > 0 } found => found.Value,
            SecretResult.Unavailable unavailable => throw new ModIoUnavailableException($"The stored mod.io token cannot be read: {unavailable.Reason}"),
            _ => throw new ModIoNotSignedInException(),
        };

    // The token rides in the header, never in the address, so no message or log line below can carry it.
    private async Task<HttpResponseMessage> Send(HttpMethod method, Uri uri, string token, HttpContent? content, CancellationToken ct)
    {
        using var slot = await throttle.Enter(Host, ct);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ModIoUnavailableException("The app could not reach mod.io.", e);
        }
    }

    private async Task ThrowUnlessSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        switch (response.StatusCode)
        {
            case HttpStatusCode.TooManyRequests:
                throw new ModIoRateLimitedException(WaitOf(response));
            case HttpStatusCode.Unauthorized:
                throw new ModIoTokenRejectedException();
            case HttpStatusCode.Forbidden:
                var error = await ErrorOf(response, ct);
                throw error?.ErrorRef switch
                {
                    MissingWriteScope => ModIoRefusedException.ReadOnlyToken(),
                    TermsNotAgreed => new ModIoTermsNotAcceptedException(),
                    _ => new ModIoRefusedException(error?.Message is { Length: > 0 } message ? $"mod.io refused the request: {message}" : "mod.io refused the request."),
                };
            default:
                throw new ModIoUnavailableException($"mod.io answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }

    private static async Task<ErrorBody?> ErrorOf(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return (await response.Content.ReadFromJsonAsync<ErrorReply>(Options, ct))?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
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
            throw new ModIoReplyUnreadableException(uri, e);
        }
    }

    private sealed record ListReply<T>(
        [property: JsonPropertyName("data")] IReadOnlyList<T>? Data,
        [property: JsonPropertyName("result_total")] int ResultTotal,
        [property: JsonPropertyName("result_offset")] int ResultOffset);

    private sealed record ErrorReply([property: JsonPropertyName("error")] ErrorBody? Error);

    private sealed record ErrorBody(
        [property: JsonPropertyName("error_ref")] int ErrorRef,
        [property: JsonPropertyName("message")] string? Message);
}
