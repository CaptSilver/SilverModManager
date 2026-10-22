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
    /// <exception cref="BepInExLayoutException">The archive name cannot be used as a folder name.</exception>
    public static string For(string archiveFileName, string? manifestNamespace = null, string? manifestName = null)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        var withoutVersion = StripVersion(stem);
        if (withoutVersion.Length != stem.Length) return RequireSafeFolderName(withoutVersion, archiveFileName);

        // The manifest comes from the archive, so its fields only count when they are valid Thunderstore names.
        // Anything else would let "../../x" put the folder outside BepInEx.
        if (manifestName is not null && IsThunderstoreName(manifestName))
        {
            if (string.IsNullOrWhiteSpace(manifestNamespace)) return manifestName;
            if (IsThunderstoreNamespace(manifestNamespace)) return $"{manifestNamespace}-{manifestName}";
        }

        return RequireSafeFolderName(stem, archiveFileName);
    }

    // Thunderstore package names are letters, digits and underscores; namespaces may also hold dashes.
    private static bool IsThunderstoreName(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static bool IsThunderstoreNamespace(string value) => value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_' || c == '-');

    private static string RequireSafeFolderName(string folder, string archiveFileName)
    {
        if (folder.Length == 0 || folder is "." or ".." || folder.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new BepInExLayoutException($"'{archiveFileName}' cannot be installed: its name does not make a valid folder name");
        return folder;
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
