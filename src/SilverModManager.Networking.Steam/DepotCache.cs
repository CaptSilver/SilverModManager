using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Steam.Values;
using NexusMods.Paths;

namespace SilverModManager.Networking.Steam;

/// <summary>
/// Locates manifests in the depotcache folders the Steam client fills.
/// </summary>
internal sealed class DepotCache
{
    /// <summary>
    /// Steam's folder name for saved manifests, under the client root and, on newer builds, under a library's
    /// steamapps folder. Files in it are named <c>{depotid}_{manifestid}.manifest</c>.
    /// </summary>
    private const string DirectoryName = "depotcache";

    private readonly ILogger _logger;

    public DepotCache(ILogger logger) => _logger = logger;

    /// <summary>
    /// Matches on the manifest id alone: older manifests of the same depot sit next to the current one, and the
    /// depot id is not known to callers.
    /// </summary>
    public bool TryGetManifestPath(AbsolutePath steamRoot, ManifestId id, AbsolutePath? installPath, out AbsolutePath path)
    {
        if (TryFind(steamRoot / DirectoryName, id, out path))
            return true;

        // An install sits at <library>/steamapps/common/<game>, so the library's steamapps folder is the grandparent.
        if (installPath is { } install && TryGetSteamApps(install, out var steamApps) && TryFind(steamApps / DirectoryName, id, out path))
            return true;

        path = default;
        return false;
    }

    private static bool TryGetSteamApps(AbsolutePath install, out AbsolutePath steamApps)
    {
        steamApps = default;
        var common = Path.GetDirectoryName(install.ToString());
        var parent = common is null ? null : Path.GetDirectoryName(common);
        if (parent is null) return false;

        steamApps = install.FileSystem.FromUnsanitizedFullPath(parent);
        return true;
    }

    private bool TryFind(AbsolutePath directory, ManifestId id, out AbsolutePath path)
    {
        path = default;
        if (!directory.DirectoryExists()) return false;

        var hits = directory.EnumerateFiles($"*_{id.Value}.manifest", recursive: false)
            .OrderBy(p => p.FileName, StringComparer.Ordinal)
            .ToArray();
        if (hits.Length == 0) return false;

        if (hits.Length > 1)
            _logger.LogWarning("{Count} manifests in {Directory} carry manifest id {ManifestId}; using {Path}", hits.Length, directory, id.Value, hits[0]);

        path = hits[0];
        return true;
    }
}
