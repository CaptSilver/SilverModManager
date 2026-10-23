using NexusMods.Abstractions.Secrets;

namespace NexusMods.Networking.GitHub;

/// <summary>
/// The secret store keys the GitHub client reads.
/// </summary>
public static class GitHubSecretKeys
{
    /// <summary>
    /// A personal access token, which raises GitHub's rate limit for the update check and release downloads.
    /// </summary>
    public static readonly SecretKey Token = new("github.token");

    public static readonly SecretKey[] All = [Token];

    /// <summary>
    /// The conventional environment variable that supplies <see cref="Token"/>; the secret overlay registers it as an alias.
    /// </summary>
    public const string TokenEnvironmentVariable = "GITHUB_TOKEN";
}
