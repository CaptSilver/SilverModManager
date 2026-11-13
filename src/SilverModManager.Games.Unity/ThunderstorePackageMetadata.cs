namespace SilverModManager.Games.Unity;

/// <summary>
/// The files Thunderstore puts at the root of every package to describe it. They are the store's, not the mod's, so no
/// loader's installer copies them into the game folder.
/// </summary>
public static class ThunderstorePackageMetadata
{
    // Compared lower-cased, as r2modman's installers do.
    private static readonly string[] Files = ["manifest.json", "readme.md", "icon.png", "changelog.md"];

    /// <summary>True for one of those files directly at the package root; <paramref name="relativePath"/> is relative to that root.</summary>
    public static bool IsAtRoot(string relativePath) => !relativePath.Contains('/') && Files.Contains(relativePath.ToLowerInvariant());
}
