using NexusMods.Hashing.xxHash3;
using NexusMods.HyperDuck;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Abstractions.Loadouts.Synchronizers;

/// <summary>
/// Reads the paths recorded as game-installed. Public because games outside this project call it.
/// </summary>
public static class GameInstalledPaths
{
    /// <summary>
    /// Every game-installed path of the loadout with the hash and size it had at the last reconcile.
    /// </summary>
    public static IReadOnlyDictionary<GamePath, (Hash Hash, Size Size)> For(IDb db, LoadoutId loadoutId)
    {
        var result = new Dictionary<GamePath, (Hash Hash, Size Size)>();
        // TODO: https://github.com/Nexus-Mods/NexusMods.MnemonicDB/issues/183
        var rows = db.Connection.Query<(LocationId Location, RelativePath Path, Hash Hash, Size Size)>(
            $"SELECT Path.Item2, Path.Item3, Hash, Size FROM mdb_GameInstalledFile(Db => {db}) WHERE Loadout = {loadoutId}"
        );
        foreach (var row in rows) result[new GamePath(row.Location, row.Path)] = (row.Hash, row.Size);
        return result;
    }
}
