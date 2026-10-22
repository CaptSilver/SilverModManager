namespace NexusMods.Games.Unity.BepInEx;

/// <summary>
/// The folder name a package gets below BepInEx's plugins, patchers, core and monomod folders. It matches the
/// "Namespace-Name" form r2modman uses, so the two managers agree on where a package lives.
/// </summary>
public static class BepInExPackageFolder
{
    /// <summary>
    /// Thunderstore downloads are named "Namespace-Name-Version.zip", which already holds the answer once the
    /// version is cut off. Anything else falls back to the manifest, and without one to the file name.
    /// </summary>
    /// <param name="archiveFileName">The archive's file name, with its extension.</param>
    /// <param name="manifestNamespace">The manifest's optional namespace field.</param>
    /// <param name="manifestName">The manifest's name field, if the archive has a manifest.</param>
    public static string For(string archiveFileName, string? manifestNamespace = null, string? manifestName = null)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        var withoutVersion = StripVersion(stem);
        if (withoutVersion.Length != stem.Length) return withoutVersion;

        if (!string.IsNullOrWhiteSpace(manifestName))
            return string.IsNullOrWhiteSpace(manifestNamespace) ? manifestName : $"{manifestNamespace}-{manifestName}";

        return stem;
    }

    // Namespaces may contain "-" but versions cannot, so only the part after the last dash can be the version.
    private static string StripVersion(string stem)
    {
        var dash = stem.LastIndexOf('-');
        if (dash <= 0 || dash == stem.Length - 1) return stem;

        foreach (var c in stem.AsSpan(dash + 1))
        {
            if (!char.IsAsciiDigit(c) && c != '.') return stem;
        }
        return stem[..dash];
    }
}
