using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.ModIo;

internal static class ModIoSecretKeys
{
    /// <summary>
    /// The user's own mod.io access token, created at mod.io/me/access with the read and write scopes. Personal, so it
    /// is only ever read from the secret store.
    /// </summary>
    public static readonly SecretKey Token = WellKnownSecrets.ModIoToken;
}
