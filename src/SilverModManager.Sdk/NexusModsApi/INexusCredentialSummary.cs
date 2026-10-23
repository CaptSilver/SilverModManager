namespace SilverModManager.Sdk.NexusModsApi;

/// <summary>
/// Which kind of Nexus credential a process holds.
/// </summary>
public enum NexusCredentialKind
{
    None,
    ApiKey,
    OAuthToken,
}

/// <summary>
/// What a process knows about its Nexus credential without calling Nexus: the kind and where it comes from.
/// For dry-run reports and the sign-in tooltip; never carries the credential itself.
/// </summary>
public interface INexusCredentialSummary
{
    NexusCredentialKind Kind { get; }

    /// <summary>
    /// Where the credential comes from, e.g. <c>--secrets-file /path</c>, <c>environment variable NEXUS_API_KEY</c>,
    /// <c>Secret Service</c> or <c>private file</c>. Empty when <see cref="Kind"/> is <see cref="NexusCredentialKind.None"/>.
    /// </summary>
    string Source { get; }
}
