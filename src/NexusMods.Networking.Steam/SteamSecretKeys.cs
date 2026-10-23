using NexusMods.Abstractions.Secrets;

namespace NexusMods.Networking.Steam;

internal static class SteamSecretKeys
{
    /// <summary>
    /// The Steam refresh token and account name, as the JSON of <see cref="DTOs.AuthData"/>.
    /// </summary>
    public static readonly SecretKey Auth = new("steam.auth");
}
