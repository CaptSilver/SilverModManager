namespace SilverModManager.Networking.Thunderstore;

/// <summary>
/// Where Thunderstore serves a package version's icon.
/// </summary>
public static class ThunderstoreIcons
{
    private const string IconBase = "https://gcdn.thunderstore.io/live/repository/icons/";

    /// <summary>
    /// The icon URL of a version, built from the same namespace, name and version the library stores for a
    /// downloaded package. It matches the <c>icon</c> field of the package listings, so a row can show its icon
    /// without the catalog being loaded.
    /// </summary>
    public static Uri UrlFor(string @namespace, string name, string versionNumber)
    {
        return new Uri($"{IconBase}{Uri.EscapeDataString($"{@namespace}-{name}-{versionNumber}")}.png");
    }
}
