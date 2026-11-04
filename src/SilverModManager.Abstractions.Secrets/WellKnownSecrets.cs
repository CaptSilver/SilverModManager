namespace SilverModManager.Abstractions.Secrets;

/// <summary>
/// The secrets that also have a conventional environment variable, e.g. <c>NEXUS_API_KEY</c>. The key classes of
/// the projects that use them (which the secret store cannot reference) and the overlay that reads the variables
/// both take the names from here.
/// </summary>
public static class WellKnownSecrets
{
    public static readonly SecretKey NexusApiKey = new("nexus.api-key");

    public const string NexusApiKeyVariable = "NEXUS_API_KEY";

    public static readonly SecretKey GitHubToken = new("github.token");

    public const string GitHubTokenVariable = "GITHUB_TOKEN";

    /// <summary>
    /// Each key with the environment variable that supplies it.
    /// </summary>
    public static IReadOnlyDictionary<SecretKey, string> EnvironmentAliases { get; } = new Dictionary<SecretKey, string>
    {
        [NexusApiKey] = NexusApiKeyVariable,
        [GitHubToken] = GitHubTokenVariable,
    };
}
