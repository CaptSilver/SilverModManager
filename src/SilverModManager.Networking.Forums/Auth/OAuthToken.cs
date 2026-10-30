using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SilverModManager.Networking.Forums.Auth;

/// <summary>
/// The body of a forum's token endpoint reply. IPS sends <c>expires_in</c> but no creation time, so the
/// expiry is worked out from the moment the reply arrived (see <see cref="OAuthToken.FromReply"/>).
/// </summary>
public sealed record OAuthTokenReply
{
    [JsonPropertyName("access_token")] public string? AccessToken { get; init; }
    [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
    [JsonPropertyName("expires_in")] public long ExpiresIn { get; init; }
}

/// <summary>
/// A forum OAuth token as it is kept in the secret store: one JSON document per site, so a refresh replaces
/// the access token, refresh token and expiry together.
/// </summary>
public sealed record OAuthToken(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string? RefreshToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt)
{
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True once <paramref name="now"/> is within five minutes of expiry, so a request does not set out with a
    /// token that lapses on the way. The caller supplies the clock.
    /// </summary>
    public bool HasExpired(DateTimeOffset now) => ExpiresAt - Margin <= now;

    /// <summary>
    /// False for a public client the forum issued no refresh token to; the user then signs in again.
    /// </summary>
    [JsonIgnore]
    public bool CanRefresh => RefreshToken is not null;

    // The tokens are credentials; they must not end up in a log through a record's generated ToString.
    public override string ToString() => $"OAuthToken {{ ExpiresAt = {ExpiresAt:O} }}";

    /// <summary>
    /// The token a reply describes, or null when it has no access token.
    /// </summary>
    public static OAuthToken? FromReply(OAuthTokenReply reply, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(reply.AccessToken)) return null;
        return new OAuthToken(reply.AccessToken, reply.RefreshToken, now + TimeSpan.FromSeconds(reply.ExpiresIn));
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Parses what <see cref="ToJson"/> wrote. False for anything else, including a document missing the access
    /// token or the expiry.
    /// </summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out OAuthToken? token)
    {
        try
        {
            token = JsonSerializer.Deserialize<OAuthToken>(json);
            if (token is { AccessToken.Length: > 0 } && token.ExpiresAt != default) return true;
        }
        catch (JsonException)
        {
        }

        token = null;
        return false;
    }
}
