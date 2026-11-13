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
    /// Reads the records of <paramref name="plugins"/> and the archives each loads, from <paramref name="archives"/>
    /// (the archive files of the Data folder).
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

        // Every plugin's raw records first: a Morrowind plugin is resolved against its masters' records and a Starfield
        // one against its masters' scales, so each needs the others read.
        var raw = new Dictionary<string, PluginRecords?>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(plugins, options, async (plugin, _) =>
        {
            // An OpenMW script list is text, not a record file, and defines no records.
            var records = IsScriptList(plugin.Name) ? PluginRecords.None : await cache.Records(plugin.Identity, plugin.Name);
            lock (raw) raw[plugin.Name] = records;
        });

        var byName = plugins.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var perPlugin = new Dictionary<string, PluginOverlap>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var plugin in plugins)
        {
            var records = Resolve(game, plugin, byName, raw);
            var sets = AssociatedArchives.Find(game, plugin.Name, archiveIdentities.Keys).Select(a => assets[a]).OfType<ArchiveAssetSet>();

            perPlugin[plugin.Name] = new PluginOverlap(records, ArchiveAssetSet.Union(sets));
            if (records is null) complete = false;
        }

        if (!complete) logger.LogInformation("Some plugins' records could not be read, so LOOT's record overlap rules were applied without them");
        return new LootOverlapData(perPlugin, complete);
    }

    private static bool IsScriptList(string pluginName) => pluginName.EndsWith(".omwscripts", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The plugin's records resolved against its masters, or null when they cannot be: the plugin or a master the
    /// resolution needs could not be read or is not in the load order, or (Starfield) a header has no scale to take.
    /// </summary>
    private PluginRecordIds? Resolve(
        LootGame game, PluginLoadoutData plugin, IReadOnlyDictionary<string, PluginLoadoutData> byName, IReadOnlyDictionary<string, PluginRecords?> raw)
    {
        if (raw[plugin.Name] is not { } own) return null;

        if (format == PluginFormat.Tes3)
        {
            var masterIds = new List<ulong[]>(plugin.Masters.Count);
            foreach (var master in plugin.Masters)
            {
                if (!raw.TryGetValue(master, out var masterRecords) || masterRecords is null) return null;
                masterIds.Add(masterRecords.NamedIds);
            }

            return PluginRecordIds.ResolveNamed(own.NamedIds, masterIds);
        }

        if (game != LootGame.Starfield) return PluginRecordIds.Resolve(plugin.Name, plugin.Masters, own.FormIds);

        if (plugin.Scale is not { } scale) return null;

        var masters = new List<(string Name, PluginScale Scale)>(plugin.Masters.Count);
        foreach (var master in plugin.Masters)
        {
            if (!byName.TryGetValue(master, out var found) || found.Scale is not { } masterScale) return null;
            masters.Add((master, masterScale));
        }

        return PluginRecordIds.ResolveStarfield(plugin.Name, scale, masters, own.FormIds);
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
internal sealed record LootOverlapSource(LootOverlapReader Reader, Func<LoadoutId, IDb, LootGame, IReadOnlyList<(string Name, FileIdentity Identity)>> Archives);
