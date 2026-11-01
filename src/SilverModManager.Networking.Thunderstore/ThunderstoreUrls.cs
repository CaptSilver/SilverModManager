using JetBrains.Annotations;
using SilverModManager.Sdk.Thunderstore;

namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// Addresses of a package's pages on thunderstore.io. A package is always shown under a community, so the
/// community slug is part of every address.
/// </summary>
[PublicAPI]
public static class ThunderstoreUrls
{
    /// <summary>
    /// The package's page in the community, e.g. <c>https://thunderstore.io/c/repo/p/Zehs/REPOLib/</c>.
    /// </summary>
    public static Uri PackagePage(string community, ThunderstorePackageRef package)
        => new($"https://thunderstore.io/c/{Uri.EscapeDataString(community)}/p/{package.Namespace}/{package.Name}/");

    /// <summary>
    /// The package's changelog tab, the page of the package with <c>changelog/</c> added.
    /// </summary>
    public static Uri Changelog(string community, ThunderstorePackageRef package)
        => new(PackagePage(community, package), "changelog/");
}
