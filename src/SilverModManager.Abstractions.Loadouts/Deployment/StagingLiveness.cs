using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts.Deployment;

/// <summary>
/// Finds the staging store that files in a folder of an installation link from right now, or null when
/// that can't be told (the game isn't located, the location is gone).
/// </summary>
public delegate ILooseStagingStore? StagingStoreLookup(GameInstallMetadataId installation, DeploymentBackendId backend, GamePath folder);

/// <summary>
/// Which staged files each store has to keep. A hash is live in the store the deployed file links from
/// and nowhere else: a copy left behind in another store, say on the filesystem a game was moved off,
/// has no link to it and is garbage.
/// </summary>
public sealed class StagingLiveness
{
    private readonly Dictionary<AbsolutePath, Dictionary<GameInstallMetadataId, HashSet<FileIdentity>>> _byStore = new();
    private readonly Dictionary<GameInstallMetadataId, HashSet<FileIdentity>> _everywhere = new();

    /// <summary>
    /// Keeps <paramref name="identity"/> in the store rooted at <paramref name="storeRoot"/> only.
    /// </summary>
    public void Add(AbsolutePath storeRoot, GameInstallMetadataId installation, FileIdentity identity)
    {
        if (!_byStore.TryGetValue(storeRoot, out var installations)) _byStore[storeRoot] = installations = new();
        Add(installations, installation, identity);
    }

    /// <summary>
    /// Keeps <paramref name="identity"/> in every store. For a file whose store can't be told, so none is
    /// swept out from under a link that might point into it.
    /// </summary>
    public void AddEverywhere(GameInstallMetadataId installation, FileIdentity identity) => Add(_everywhere, installation, identity);

    /// <summary>
    /// Liveness that holds the same hashes in every store.
    /// </summary>
    public static StagingLiveness Everywhere(IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<FileIdentity>> live)
    {
        var liveness = new StagingLiveness();
        foreach (var (installation, hashes) in live)
        {
            if (hashes.Count == 0) liveness._everywhere.TryAdd(installation, []);
            foreach (var identity in hashes) liveness.AddEverywhere(installation, identity);
        }
        return liveness;
    }

    /// <summary>
    /// What the store rooted at <paramref name="storeRoot"/> keeps. An installation that is missing loses
    /// its directory in that store.
    /// </summary>
    public IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<FileIdentity>> For(AbsolutePath storeRoot)
    {
        var result = new Dictionary<GameInstallMetadataId, HashSet<FileIdentity>>();
        foreach (var (installation, hashes) in _everywhere) result[installation] = [..hashes];

        if (_byStore.TryGetValue(storeRoot, out var own))
        {
            foreach (var (installation, hashes) in own)
            {
                if (!result.TryGetValue(installation, out var merged)) result[installation] = merged = [];
                merged.UnionWith(hashes);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<FileIdentity>)kv.Value);
    }

    private static void Add(Dictionary<GameInstallMetadataId, HashSet<FileIdentity>> installations, GameInstallMetadataId installation, FileIdentity identity)
    {
        if (!installations.TryGetValue(installation, out var hashes)) installations[installation] = hashes = [];
        // Staged names are xxHash3 keys. Another kind would never match a staged file, and the sweep would delete it.
        hashes.Add(identity.Kind == HashKind.XxHash3 ? identity : throw new InvalidOperationException($"{identity} is a {identity.Kind} identity, not an xxHash3"));
    }
}
