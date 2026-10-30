using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Forums;

/// <summary>
/// The secret store keys the forum tokens live under, one per site so a refresh replaces the access token,
/// refresh token and expiry in one write.
/// </summary>
public static class ForumSecretKeys
{
    /// <summary>
    /// The LoversLab token, as the JSON of <see cref="Auth.OAuthToken"/>.
    /// </summary>
    public static readonly SecretKey LoversLab = new("loverslab.oauth");

    /// <summary>
    /// The VectorPlexus token, as the JSON of <see cref="Auth.OAuthToken"/>.
    /// </summary>
    public static readonly SecretKey VectorPlexus = new("vectorplexus.oauth");

    public static readonly SecretKey[] All = [LoversLab, VectorPlexus];
}
