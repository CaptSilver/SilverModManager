using NexusMods.Paths;

namespace SilverModManager.Abstractions.Steam;

public static class DepotManifestSourceExtensions
{
    /// <summary>
    /// The SHA-1 of every file of a cached manifest, or null when the file cannot be read. A source that lists the hashes
    /// separately answers even where the file names are encrypted; any other has to parse the whole manifest, which
    /// needs them readable.
    /// </summary>
    public static ManifestFileHashes? TryReadFileHashes(this IDepotManifestSource source, AbsolutePath manifestPath)
    {
        if (source is IDepotManifestCatalog catalog) return catalog.ReadFileHashes(manifestPath);

        return source.Read(manifestPath) is LocalManifestResult.Parsed parsed
            ? new ManifestFileHashes(parsed.Manifest.DepotId, parsed.Manifest.ManifestId, parsed.Manifest.Files.Select(f => f.Hash).ToHashSet())
            : null;
    }
}
