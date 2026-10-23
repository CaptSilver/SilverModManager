using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.App.UI.Controls.TopBar;

internal static class AccountTooltipText
{
    /// <summary>
    /// The sign-in tooltip. When the credential was supplied from outside the store, says so, because signing out
    /// then changes nothing for the next start.
    /// </summary>
    public static string Describe(string? username, INexusCredentialSummary credential)
    {
        var text = $"Logged in to Nexus Mods as {username}";
        if (credential.Kind == NexusCredentialKind.None) return text;

        // The prefixes are the ones OverlaySecretStore gives an environment variable or a --secrets-file value.
        var source = credential.Source;
        if (source.StartsWith("environment variable", StringComparison.Ordinal)) return $"{text} via environment variable";
        if (source.StartsWith("--secrets-file", StringComparison.Ordinal)) return $"{text} via --secrets-file";
        return text;
    }
}
