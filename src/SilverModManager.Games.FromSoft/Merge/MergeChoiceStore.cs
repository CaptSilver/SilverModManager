using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Writes a loadout's <see cref="LoadoutMergeChoice"/> rows. The verbs and anything else that pins a merge winner go
/// through here, so a row is always written the way <see cref="MergeChoices"/> reads it.
/// </summary>
public sealed class MergeChoiceStore(IConnection connection)
{
    /// <summary>Pins <paramref name="winner"/> for one entry, replacing the pin the entry already had.</summary>
    /// <param name="nexusModId">The winner's Nexus mod id, kept so the pin survives the mod being reinstalled as a new group.</param>
    public async Task Upsert(LoadoutId loadout, string path, string key, LoadoutItemGroup.ReadOnly winner, ModId? nexusModId)
    {
        using var tx = connection.BeginTransaction();
        foreach (var old in Find(loadout, path, key)) tx.Delete(old.Id, recursive: false);
        var row = new LoadoutMergeChoice.New(tx)
        {
            LoadoutId = loadout,
            Path = path,
            Key = key,
            WinnerGroupId = LoadoutItemGroupId.From(winner.Id),
        };
        if (nexusModId is { } mod) row.WinnerModId = mod.Value;
        await tx.Commit();
    }

    /// <summary>Removes the pin on one entry; returns how many rows went.</summary>
    public async Task<int> Delete(LoadoutId loadout, string path, string key)
        => await Remove(Find(loadout, path, key).ToArray());

    /// <summary>Removes every pin of the loadout, or only those of one path; returns how many rows went.</summary>
    public async Task<int> Clear(LoadoutId loadout, string? path)
        => await Remove(LoadoutMergeChoice.FindByLoadout(connection.Db, loadout)
            .Where(c => path is null || c.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray());

    private IEnumerable<LoadoutMergeChoice.ReadOnly> Find(LoadoutId loadout, string path, string key)
        => LoadoutMergeChoice.FindByLoadout(connection.Db, loadout)
            .Where(c => c.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && c.Key == key);

    private async Task<int> Remove(LoadoutMergeChoice.ReadOnly[] rows)
    {
        if (rows.Length == 0) return 0;
        using var tx = connection.BeginTransaction();
        foreach (var row in rows) tx.Delete(row.Id, recursive: false);
        await tx.Commit();
        return rows.Length;
    }
}
