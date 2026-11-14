using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.Steam;

internal static class SteamSecretKeys
{
    /// <summary>
    /// The Steam refresh token and account name, as the JSON of <see cref="DTOs.AuthData"/>.
    /// </summary>
    public static readonly SecretKey Auth = new("steam.auth");

    /// <summary>
    /// The user's own Steam Web API key, which Steam's Workshop search asks for. Personal and confidential, so it is
    /// never shipped and only ever read from the secret store.
    /// </summary>
    public static readonly SecretKey WebApiKey = WellKnownSecrets.SteamWebApiKey;
}
