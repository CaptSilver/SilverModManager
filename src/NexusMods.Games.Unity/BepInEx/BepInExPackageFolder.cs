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

    /// <summary>The "namespace" and "name" fields of a package's manifest.json.</summary>
    public readonly record struct Manifest(string? Namespace, string? Name);

    /// <summary>
    /// The folder for a library item. <paramref name="readManifest"/> is called only when the item's name has no version
    /// suffix, because the manifest is then the only source of the namespace and reading it costs a file store load.
    /// </summary>
    public static async ValueTask<string> FromLibraryItemName(string libraryItemName, Func<ValueTask<Manifest?>> readManifest)
    {
        var stem = Path.GetFileNameWithoutExtension(libraryItemName);
        if (StripVersion(stem).Length != stem.Length) return For(libraryItemName);

        var manifest = await readManifest();
        return For(libraryItemName, manifest?.Namespace, manifest?.Name);
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
