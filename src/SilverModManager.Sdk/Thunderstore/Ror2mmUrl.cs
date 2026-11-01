using JetBrains.Annotations;

namespace SilverModManager.Sdk.Thunderstore;

/// <summary>
/// <c>ror2mm://v1/install/thunderstore.io/{namespace}/{name}/{version}/</c>, the link behind Thunderstore's
/// "Install with App" button. It names one package version and no community.
/// </summary>
[PublicAPI]
public readonly record struct Ror2mmUrl(ThunderstoreVersionRef Version)
{
    /// <summary>The URI scheme Thunderstore uses for these links.</summary>
    public const string Scheme = "ror2mm";

    private const string Expected = "ror2mm://v1/install/thunderstore.io/{namespace}/{name}/{version}/";
    private const string Domain = "thunderstore.io";

    /// <summary>
    /// Parses an install link. On failure <paramref name="problem"/> says what is wrong and quotes the link.
    /// </summary>
    public static bool TryParse(Uri uri, out Ror2mmUrl url, out string problem)
    {
        url = default;

        // Segments after the leading "/": install, host, namespace, name, version.
        var segments = uri.Segments.Skip(1).Select(s => Uri.UnescapeDataString(s.TrimEnd('/'))).ToArray();
        var isInstallLink = uri.Scheme.Equals(Scheme, StringComparison.OrdinalIgnoreCase)
            && uri.Host == "v1"
            && segments is ["install", var host, _, _, _]
            && IsThunderstoreHost(host);

        if (isInstallLink
            && ThunderstorePackageRef.TryCreate(segments[2], segments[3], out var package)
            && ThunderstoreVersionNumber.TryParse(segments[4], out var version))
        {
            url = new Ror2mmUrl(new ThunderstoreVersionRef(package, version));
            problem = string.Empty;
            return true;
        }

        problem = $"Cannot open '{uri.OriginalString}': only {Expected} links with a valid package and version are understood.";
        return false;
    }

    // r2modman accepts one optional subdomain label and ignores it.
    private static bool IsThunderstoreHost(string host)
    {
        if (host.Equals(Domain, StringComparison.OrdinalIgnoreCase)) return true;
        var suffix = "." + Domain;
        if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) is false) return false;
        var label = host[..^suffix.Length];
        return label.Length > 0 && label.All(char.IsAsciiLetterOrDigit);
    }
}
