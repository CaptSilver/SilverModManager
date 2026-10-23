using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Networking.NexusWebApi.Auth;

/// <summary>
/// The secret store keys the Nexus credentials live under.
/// </summary>
public static class NexusSecretKeys
{
    /// <summary>
    /// A personal API key.
    /// </summary>
    public static readonly SecretKey ApiKey = new("nexus.api-key");

    /// <summary>
    /// The OAuth token, as the JSON of <see cref="NexusOAuthToken"/>.
    /// </summary>
    public static readonly SecretKey OAuth = new("nexus.oauth");

    public static readonly SecretKey[] All = [ApiKey, OAuth];

    /// <summary>
    /// The conventional environment variable that supplies <see cref="ApiKey"/>; the secret overlay
    /// registers it as an alias.
    /// </summary>
    public const string ApiKeyEnvironmentVariable = "NEXUS_API_KEY";
}
