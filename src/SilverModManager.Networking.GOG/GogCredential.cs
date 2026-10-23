using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace NexusMods.Networking.GOG;

/// <summary>
/// The GOG login as it is kept in the secret store (under <see cref="GogSecretKeys.OAuth"/>).
/// </summary>
public sealed record GogCredential(
    string AccessToken,
    string RefreshToken,
    string SessionId,
    ulong UserId,
    DateTimeOffset ExpiresAt)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // The tokens are credentials; they must not end up in a log through a record's generated ToString.
    public override string ToString() => $"GogCredential {{ UserId = {UserId}, ExpiresAt = {ExpiresAt:O} }}";

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parses what <see cref="ToJson"/> wrote. False for anything else, including a document missing a token.
    /// </summary>
    public static bool TryParse(string json, [NotNullWhen(true)] out GogCredential? credential)
    {
        try
        {
            credential = JsonSerializer.Deserialize<GogCredential>(json, JsonOptions);
            if (credential is { AccessToken: not null, RefreshToken: not null, SessionId: not null }) return true;
        }
        catch (JsonException)
        {
        }

        credential = null;
        return false;
    }
}
