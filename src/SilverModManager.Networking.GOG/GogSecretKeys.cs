using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.GOG;

/// <summary>
/// The secret store keys the GOG login lives under.
/// </summary>
public static class GogSecretKeys
{
    /// <summary>
    /// The OAuth login, as the JSON of <see cref="GogCredential"/>. One value, so a refresh replaces
    /// the access token, refresh token and expiry together.
    /// </summary>
    public static readonly SecretKey OAuth = new("gog.oauth");

    public static readonly SecretKey[] All = [OAuth];
}
