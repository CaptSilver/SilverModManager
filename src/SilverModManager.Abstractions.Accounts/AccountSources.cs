using SilverModManager.Abstractions.Secrets;

namespace SilverModManager.Abstractions.Accounts;

/// <summary>Words for where a stored credential lives, shared by every account row that keeps one in the secret store.</summary>
public static class AccountSources
{
    /// <summary>
    /// Where <paramref name="key"/> is kept under <paramref name="status"/>: the backend's name, or the override that
    /// supplies it.
    /// </summary>
    public static string Describe(SecretStoreStatus status, SecretKey key)
    {
        if (status.OverriddenKeys.Contains(key)) return "an environment variable or secrets file";
        return status.Backend switch
        {
            SecretBackendKind.SecretService => "Secret Service",
            SecretBackendKind.File => "private file",
            SecretBackendKind.Memory => "in-memory store",
            _ => status.Detail,
        };
    }
}
