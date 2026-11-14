using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SilverModManager.Networking.Steam.Workshop.DTOs;

namespace SilverModManager.Networking.Steam.Workshop;

internal sealed class SteamWorkshopApi(HttpClient http) : ISteamWorkshopApi
{
    private static readonly Uri Endpoint = new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");

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

    private sealed record Reply([property: JsonPropertyName("response")] Body? Response);

    private sealed record Body([property: JsonPropertyName("publishedfiledetails")] IReadOnlyList<PublishedFileDetails>? Details);
}
