namespace SilverModManager.Games.Unity;

/// <summary>
/// The <c>Namespace-Name-Version</c> form Thunderstore names its downloads and packages in. Every loader of a Unity
/// game reads it, so the parsing lives here rather than with one loader.
/// </summary>
public static class ThunderstorePackageName
{
    /// <summary>
    /// <paramref name="stem"/> without a trailing <c>-1.2.3</c>. Namespaces may contain dashes but versions cannot, so only
    /// the part after the last dash can be the version. A stem with no such suffix comes back unchanged.
    /// </summary>
    public static string StripVersion(string stem)
    {
        var dash = stem.LastIndexOf('-');
        if (dash <= 0 || dash == stem.Length - 1) return stem;

        foreach (var c in stem.AsSpan(dash + 1))
        {
            if (!char.IsAsciiDigit(c) && c != '.') return stem;
        }
        return stem[..dash];
    }

    /// <summary>The package name of a download's file name, or null when the name has no version suffix.</summary>
    public static string? FromArchiveName(string archiveFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        var withoutVersion = StripVersion(stem);
        return withoutVersion.Length == stem.Length ? null : withoutVersion;
    }

    /// <summary>Thunderstore package names are letters, digits and underscores.</summary>
    public static bool IsName(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>Namespaces may also hold dashes.</summary>
    public static bool IsNamespace(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');
}
