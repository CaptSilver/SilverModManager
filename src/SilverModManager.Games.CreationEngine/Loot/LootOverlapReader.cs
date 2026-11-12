using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.Archives;
using SilverModManager.Games.Bethesda.LoadOrder;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.CreationEngine.Loot;

/// <summary>
/// Gathers what LOOT's overlap rules need for a load order: each plugin's records resolved against its masters, and
/// the files of the archives it loads. The sorter then adds an edge between plugins that override the same records or
/// load the same files.
/// </summary>
internal sealed class LootOverlapReader(PluginOverlapCache cache, PluginFormat format, ILogger logger)
{
    /// <summary>How many plugins and archives are read at once; each read is mostly I/O, but a sort should not take the machine.</summary>
    private const int MaxParallelReads = 4;

    /// <summary>
    /// True for the games whose records can be compared by their FormIDs alone. Morrowind names its records, and
    /// Starfield's medium and small plugins resolve a FormID by the scale of each master; neither is read yet.
    /// </summary>
    public bool Covers(LootGame game) => format != PluginFormat.Tes3 && game != LootGame.Starfield;

    /// <summary>
    /// Reads the records of <paramref name="plugins"/> and the archives each loads, from <paramref name="archives"/>
    /// (the archive files of the Data folder). Only call for a game <see cref="Covers"/> says is covered.
    /// </summary>
    public async Task<LootOverlapData> Read(
        LootGame game, IReadOnlyList<PluginLoadoutData> plugins, IReadOnlyList<(string Name, FileIdentity Identity)> archives, CancellationToken token)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = token };

        // An archive several plugins load (a prefix match, or the base game's) is read once.
        var archiveIdentities = archives.ToDictionary(a => a.Name, a => a.Identity, StringComparer.OrdinalIgnoreCase);
        var neededArchives = plugins
            .SelectMany(p => AssociatedArchives.Find(game, p.Name, archiveIdentities.Keys))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var assets = new Dictionary<string, ArchiveAssetSet?>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(neededArchives, options, async (name, _) =>
        {
            var set = await cache.Assets(archiveIdentities[name], name);
            lock (assets) assets[name] = set;
        });

        var perPlugin = new Dictionary<string, PluginOverlap>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        await Parallel.ForEachAsync(plugins, options, async (plugin, _) =>
        {
            var formIds = await cache.FormIds(plugin.Identity, plugin.Name);
            var records = formIds is null ? null : PluginRecordIds.Resolve(plugin.Name, plugin.Masters, formIds);

            var sets = AssociatedArchives.Find(game, plugin.Name, archiveIdentities.Keys).Select(a => assets[a]).OfType<ArchiveAssetSet>();

            lock (perPlugin)
            {
                perPlugin[plugin.Name] = new PluginOverlap(records, ArchiveAssetSet.Union(sets));
                if (records is null) complete = false;
            }
        });

        if (!complete) logger.LogInformation("Some plugins' records could not be read, so LOOT's record overlap rules were applied without them");
        return new LootOverlapData(perPlugin, complete);
    }
}

/// <param name="Records">Null when the plugin's records could not be read.</param>
internal sealed record PluginOverlap(PluginRecordIds? Records, ArchiveAssetSet Assets);

/// <param name="Complete">False when some plugin's records could not be read, so the rules ran on part of the data.</param>
internal sealed record LootOverlapData(IReadOnlyDictionary<string, PluginOverlap> Plugins, bool Complete);

/// <summary>
/// The reader and the query for the archive files of a loadout's Data folder, which only the plugin order knows how
/// to ask.
/// </summary>
/// <param name="Archives">The enabled archives of the game's kind directly in the loadout's Data folder, by file name.</param>
internal sealed record LootOverlapSource(LootOverlapReader Reader, Func<LoadoutId, IDb, LootGame, IReadOnlyList<(string Name, FileIdentity Identity)>> Archives)
{
    public bool Covers(LootGame game) => Reader.Covers(game);
}
