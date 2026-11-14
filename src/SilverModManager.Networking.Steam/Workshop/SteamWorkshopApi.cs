using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilverModManager.Networking.Steam.Workshop.DTOs;

namespace SilverModManager.Networking.Steam.Workshop;

internal sealed class SteamWorkshopApi(HttpClient http) : ISteamWorkshopApi
{
    private static readonly Uri Endpoint = new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");

    private static readonly Uri QueryEndpoint = new("https://api.steampowered.com/IPublishedFileService/QueryFiles/v1/");

    // Any game will do for a count; Spacewar is the app Valve hands out for testing.
    private const uint KeyCheckAppId = 480;

    private const int PageSize = 30;

    // k_PublishedFileQueryType_RankedByTrend and _RankedByTextSearch.
    private const int RankedByTrend = 3;
    private const int RankedByTextSearch = 12;

    // Steam answers up to a hundred items per request.
    private const int BatchSize = 100;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public async Task<IReadOnlyList<PublishedFileDetails>> Details(IReadOnlyList<ulong> ids, CancellationToken ct)
    {
        var all = new List<PublishedFileDetails>(ids.Count);
        foreach (var batch in ids.Chunk(BatchSize))
            all.AddRange(await Batch(batch, ct));
        return all;
    }

    public async Task<WorkshopQueryPage> Query(uint appId, string text, string? cursor, string key, CancellationToken ct)
    {
        text = text.Trim();
        var parameters = new Dictionary<string, string>
        {
            ["key"] = key,
            ["appid"] = appId.ToString(CultureInfo.InvariantCulture),
            ["query_type"] = (text.Length > 0 ? RankedByTextSearch : RankedByTrend).ToString(CultureInfo.InvariantCulture),
            ["cursor"] = string.IsNullOrEmpty(cursor) ? "*" : cursor,
            ["numperpage"] = PageSize.ToString(CultureInfo.InvariantCulture),
            ["search_text"] = text,
            ["return_metadata"] = "true",
            ["return_previews"] = "true",
            ["return_tags"] = "true",
            // Without it Steam fills file_description, the whole page body, and leaves short_description out.
            ["return_short_description"] = "true",
        };

        using var response = await Get(parameters, "searched the Workshop", ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) throw new SteamWebApiKeyRejectedException();
        var body = await Read(response, "searched the Workshop", ct);
        // Past the last page Steam sends back the cursor it was given rather than leaving it out, so asking again
        // would return the same page forever.
        var next = string.IsNullOrEmpty(body.NextCursor) || body.NextCursor == parameters["cursor"] ? null : body.NextCursor;
        return new WorkshopQueryPage(body.Details ?? [], next, body.Total ?? 0);
    }

    public async Task<bool> IsKeyAccepted(string key, CancellationToken ct)
    {
        var parameters = new Dictionary<string, string>
        {
            ["key"] = key,
            ["appid"] = KeyCheckAppId.ToString(CultureInfo.InvariantCulture),
            ["totalonly"] = "true",
        };

        using var response = await Get(parameters, "checked the Web API key", ct);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return false;
        await Read(response, "checked the Web API key", ct);
        return true;
    }

    // The key rides in the query string, so nothing below puts the address in a message or a log.
    private async Task<HttpResponseMessage> Get(Dictionary<string, string> parameters, string what, CancellationToken ct)
    {
        var uri = new UriBuilder(QueryEndpoint) { Query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}")) }.Uri;
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(uri, ct);
        }
        catch (HttpRequestException)
        {
            throw new SteamWorkshopUnavailableException($"The app could not reach Steam when it {what}.");
        }

        if (response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new SteamWorkshopUnavailableException($"Steam answered {status} when the app {what}.");
    }

    private static async Task<QueryBody> Read(HttpResponseMessage response, string what, CancellationToken ct)
    {
        try
        {
            var reply = await response.Content.ReadFromJsonAsync<QueryReply>(Options, ct);
            return reply?.Response ?? throw new JsonException($"The reply to the request that {what} has no body.");
        }
        catch (JsonException e)
        {
            throw new SteamWorkshopReplyUnreadableException(e);
        }
    }

    private async Task<IReadOnlyList<PublishedFileDetails>> Batch(ulong[] ids, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("itemcount", ids.Length.ToString(CultureInfo.InvariantCulture)) };
        for (var i = 0; i < ids.Length; i++)
            form.Add(new($"publishedfileids[{i}]", ids[i].ToString(CultureInfo.InvariantCulture)));

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(Endpoint, new FormUrlEncodedContent(form), ct);
        }
        catch (HttpRequestException e)
        {
            throw new SteamWorkshopUnavailableException("The app could not reach Steam to ask about Workshop items.", e);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new SteamWorkshopUnavailableException($"Steam answered {(int)response.StatusCode} when asked about Workshop items.");

            try
            {
                var reply = await response.Content.ReadFromJsonAsync<Reply>(Options, ct);
                return reply?.Response?.Details ?? throw new JsonException("The reply has no item list.");
            }
            catch (JsonException e)
            {
                throw new SteamWorkshopReplyUnreadableException(e);
            }
        }
    }

    private sealed record QueryReply([property: JsonPropertyName("response")] QueryBody? Response);

    private sealed record QueryBody(
        [property: JsonPropertyName("total")] int? Total,
        [property: JsonPropertyName("publishedfiledetails")] IReadOnlyList<PublishedFileDetails>? Details,
        [property: JsonPropertyName("next_cursor")] string? NextCursor);

    private sealed record Reply([property: JsonPropertyName("response")] Body? Response);

    private sealed record Body([property: JsonPropertyName("publishedfiledetails")] IReadOnlyList<PublishedFileDetails>? Details);
}
