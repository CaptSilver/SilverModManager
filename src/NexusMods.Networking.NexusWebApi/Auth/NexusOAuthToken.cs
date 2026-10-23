using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusMods.Abstractions.NexusWebApi.DTOs.OAuth;

namespace NexusMods.Networking.NexusWebApi.Auth;

/// <summary>
/// The OAuth token as it is kept in the secret store (under <see cref="NexusSecretKeys.OAuth"/>).
/// </summary>
public sealed record NexusOAuthToken(
    [property: JsonPropertyName("accessToken")] string AccessToken,
    [property: JsonPropertyName("refreshToken")] string RefreshToken,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// True once the token is within five minutes of expiring, so a request does not set out with a token that
    /// lapses on the way.
    /// </summary>
    [JsonIgnore]
    public bool HasExpired => ExpiresAt - TimeSpan.FromMinutes(5) <= DateTimeOffset.UtcNow;

    // The tokens are credentials; they must not end up in a log through a record's generated ToString.
    public override string ToString() => $"NexusOAuthToken {{ ExpiresAt = {ExpiresAt:O} }}";

    /// <summary>
    /// The token a reply describes, or null when the reply lacks the access or refresh token.
    /// </summary>
    public static NexusOAuthToken? FromReply(JwtTokenReply reply)
    {
        if (reply.AccessToken is null || reply.RefreshToken is null) return null;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(reply.CreatedAt) + TimeSpan.FromSeconds(reply.ExpiresIn);
        return new NexusOAuthToken(reply.AccessToken, reply.RefreshToken, expiresAt);
    }

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// Parses what <see cref="ToJson"/> wrote. False for anything else, including a document missing a token.
    /// </summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out NexusOAuthToken? token)
    {
        try
        {
            token = JsonSerializer.Deserialize<NexusOAuthToken>(json);
            return token is { AccessToken: not null, RefreshToken: not null };
        }
        catch (JsonException)
        {
            token = null;
            return false;
        }
    }
}
