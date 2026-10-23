using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

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
    private readonly Dictionary<AbsolutePath, Dictionary<GameInstallMetadataId, HashSet<Hash>>> _byStore = new();
    private readonly Dictionary<GameInstallMetadataId, HashSet<Hash>> _everywhere = new();

    /// <summary>
    /// Keeps <paramref name="hash"/> in the store rooted at <paramref name="storeRoot"/> only.
    /// </summary>
    public void Add(AbsolutePath storeRoot, GameInstallMetadataId installation, Hash hash)
    {
        if (!_byStore.TryGetValue(storeRoot, out var installations)) _byStore[storeRoot] = installations = new();
        Add(installations, installation, hash);
    }

    /// <summary>
    /// Keeps <paramref name="hash"/> in every store. For a file whose store can't be told, so none is
    /// swept out from under a link that might point into it.
    /// </summary>
    public void AddEverywhere(GameInstallMetadataId installation, Hash hash) => Add(_everywhere, installation, hash);

    /// <summary>
    /// Liveness that holds the same hashes in every store.
    /// </summary>
    public static StagingLiveness Everywhere(IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>> live)
    {
        var liveness = new StagingLiveness();
        foreach (var (installation, hashes) in live)
        {
            if (hashes.Count == 0) liveness._everywhere.TryAdd(installation, []);
            foreach (var hash in hashes) liveness.AddEverywhere(installation, hash);
        }
        return liveness;
    }

    /// <summary>
    /// What the store rooted at <paramref name="storeRoot"/> keeps. An installation that is missing loses
    /// its directory in that store.
    /// </summary>
    public IReadOnlyDictionary<GameInstallMetadataId, IReadOnlySet<Hash>> For(AbsolutePath storeRoot)
    {
        var result = new Dictionary<GameInstallMetadataId, HashSet<Hash>>();
        foreach (var (installation, hashes) in _everywhere) result[installation] = [..hashes];

        if (_byStore.TryGetValue(storeRoot, out var own))
        {
            foreach (var (installation, hashes) in own)
            {
                if (!result.TryGetValue(installation, out var merged)) result[installation] = merged = [];
                merged.UnionWith(hashes);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<Hash>)kv.Value);
    }

    private static void Add(Dictionary<GameInstallMetadataId, HashSet<Hash>> installations, GameInstallMetadataId installation, Hash hash)
    {
        if (!installations.TryGetValue(installation, out var hashes)) installations[installation] = hashes = [];
        hashes.Add(hash);
    }
}
