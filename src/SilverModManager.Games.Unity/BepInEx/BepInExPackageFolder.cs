using SilverModManager.Sdk.IO;

namespace SilverModManager.Games.Unity.BepInEx;

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
    /// <exception cref="BepInExLayoutException">Nothing usable is left of the archive name once its unusable characters are replaced.</exception>
    public static string For(string archiveFileName, string? manifestNamespace = null, string? manifestName = null)
    {
        var stem = Path.GetFileNameWithoutExtension(archiveFileName);
        var package = ThunderstorePackageName.FromArchiveName(archiveFileName);
        if (package is not null) return RequireSafeFolderName(package, archiveFileName);

        // The manifest comes from the archive, so its fields only count when they are valid Thunderstore names.
        // Anything else would let "../../x" put the folder outside BepInEx.
        if (manifestName is not null && ThunderstorePackageName.IsName(manifestName))
        {
            if (string.IsNullOrWhiteSpace(manifestNamespace)) return manifestName;
            if (ThunderstorePackageName.IsNamespace(manifestNamespace)) return $"{manifestNamespace}-{manifestName}";
        }

        return RequireSafeFolderName(stem, archiveFileName);
    }

    // Sanitized rather than refused: a stray character in the archive's name should not stop the install, only a name
    // with nothing usable left.
    private static string RequireSafeFolderName(string folder, string archiveFileName)
        => FolderName.Sanitize(folder)
           ?? throw new BepInExLayoutException($"'{archiveFileName}' cannot be installed: its name does not make a valid folder name");

    /// <summary>The "namespace" and "name" fields of a package's manifest.json.</summary>
    public readonly record struct Manifest(string? Namespace, string? Name);

    /// <summary>
    /// The folder for a library item. <paramref name="readManifest"/> is called only when the item's name has no version
    /// suffix, because the manifest is then the only source of the namespace and reading it costs a file store load.
    /// </summary>
    public static async ValueTask<string> FromLibraryItemName(string libraryItemName, Func<ValueTask<Manifest?>> readManifest)
    {
        if (ThunderstorePackageName.FromArchiveName(libraryItemName) is not null) return For(libraryItemName);

        var manifest = await readManifest();
        return For(libraryItemName, manifest?.Namespace, manifest?.Name);
    }
}
