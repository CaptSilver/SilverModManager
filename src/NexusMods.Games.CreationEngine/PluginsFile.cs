using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.Bethesda.LoadOrder;
using NexusMods.Games.Bethesda.LoadOrder.Formats;
using NexusMods.Games.Bethesda.LoadOrder.Ordering;
using NexusMods.Games.CreationEngine.Abstractions;
using NexusMods.Games.CreationEngine.PluginLists;
using NexusMods.Games.CreationEngine.SortOrder;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.CreationEngine;

/// <summary>
/// The plugin list file of a game that keeps one as text. The plugins come from the sync tree, their order and
/// on/off switches from the loadout's plugin sort order; this only renders them and never writes to the database.
/// </summary>
public class PluginsFile : IIntrinsicFile
{
    private readonly ICreationEngineGame _game;
    private readonly APluginsTxtMechanism _mechanism;
    private readonly ILogger<PluginsFile> _logger;

    private record struct Metadata(RelativePath FileName, Hash Hash, PluginHeader Header);

    public PluginsFile(ILogger<PluginsFile> logger, ICreationEngineGame game, APluginsTxtMechanism mechanism)
    {
        _game = game;
        _mechanism = mechanism;
        Path = game.PluginsFile;
        _logger = logger;
    }

    public GamePath Path { get; }

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var installation = loadout.InstallationInstance;
        await Render(stream, installation, _game.EarlyLoadingPlugins(installation), Variety.StoredOrder(loadout), syncTree);
    }

    private IPluginSortOrderVariety Variety => _game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();

    /// <param name="earlyLoaders">Plugins the game loads before any list, in load order.</param>
    /// <param name="rows">The stored order and switches; a plugin without a row is on and goes where the resolver puts it.</param>
    internal async Task Render(
        Stream stream,
        GameInstallation installation,
        IReadOnlyList<string> earlyLoaders,
        IReadOnlyList<PluginSortData> rows,
        Dictionary<GamePath, SyncNode> syncTree)
    {
        var listed = await Expected(installation, earlyLoaders, rows, syncTree);
        await using var writer = new StreamWriter(stream, leaveOpen: true);
        await writer.WriteAsync(_mechanism.Render(listed));
        await writer.FlushAsync();
    }

    /// <summary>
    /// The plugin list the app wants on disk: every plugin of the sync tree in load order, with its switch.
    /// Render writes this and Ingest compares the game's file with it, so both see the same list.
    /// </summary>
    private async Task<List<PluginListEntry>> Expected(
        GameInstallation installation,
        IReadOnlyList<string> earlyLoaders,
        IReadOnlyList<PluginSortData> rows,
        Dictionary<GamePath, SyncNode> syncTree)
    {
        var plugins = new List<Metadata>();
        List<string> unreadable = [];
        foreach (var entry in syncTree)
        {
            if (entry.Key.Parent != _game.DataPath || !KnownCEExtensions.PluginFiles.Contains(entry.Key.Extension)) continue;
            if (!entry.Value.HaveLoadout) continue;

            if (await MakeMetadata(entry) is { } found)
                plugins.Add(found);
            else
                unreadable.Add(entry.Key.FileName);
        }

        // A plugin left out of Plugins.txt is disabled, so refuse rather than write a file that switches mods off.
        if (unreadable.Count > 0)
        {
            unreadable.Sort(StringComparer.OrdinalIgnoreCase);
            throw new UnreadablePluginsException(unreadable);
        }

        var rowsByName = new Dictionary<string, PluginSortData>(PluginNameComparer.Instance);
        foreach (var row in rows) rowsByName.TryAdd(row.Name, row);

        var early = new Dictionary<string, int>(PluginNameComparer.Instance);
        for (var i = 0; i < earlyLoaders.Count; i++) early.TryAdd(earlyLoaders[i], i);

        var entries = plugins.Select(plugin =>
        {
            var name = plugin.Header.FileName;
            return new PluginOrderEntry(
                name,
                _game.IsMaster(plugin.Header, installation),
                _game.IsBlueprint(plugin.Header),
                plugin.Header.Masters,
                rowsByName.TryGetValue(name, out var row) ? row.SortIndex : null,
                early.TryGetValue(name, out var earlyIndex) ? earlyIndex : null);
        }).ToList();

        var order = PluginOrderResolver.Resolve(entries);
        Debug.Assert(PluginOrderValidation.Validate(order, entries, earlyLoaders) is null,
            "The resolver produced a load order that libloadorder would reject.");
        _logger.LogDebug("Ordered {Count} plugin files", order.Count);

        var byName = entries.ToDictionary(e => e.Name, PluginNameComparer.Instance);
        return order.Select(name =>
        {
            var entry = byName[name];
            // A blueprint master never loads early, so the game does not know to load it without the list.
            var isEarly = entry.EarlyLoaderIndex is not null && !entry.IsBlueprintMaster;
            var active = isEarly || !rowsByName.TryGetValue(name, out var row) || row.IsEnabled;
            return new PluginListEntry(name, active, isEarly);
        }).ToList();
    }

    private async ValueTask<Metadata?> MakeMetadata(KeyValuePair<GamePath, SyncNode> arg)
    {
        var relPath = arg.Key.Path.FileName;
        Hash hash;

        // We need to figure out if we should look at the loadout or the disk
        var syncNode = arg.Value;
        if (syncNode.Actions.HasFlag(Actions.ExtractToDisk) || syncNode.Disk.Hash == Hash.Zero || syncNode.SourceItemType == LoadoutSourceItemType.Game)
            hash = syncNode.Loadout.Hash;
        else
            hash = syncNode.Disk.Hash;

        var modHeader = await _game.ParseNode(syncNode, hash, relPath);
        return modHeader is null ? null : new Metadata(relPath, hash, modHeader);
    }

    /// <summary>
    /// Compares the game's file with the list the app would write and records what differs as a notice. The order
    /// and switches in the database are never changed from here: the same Apply rewrites the file from them, which
    /// replaces the game's edits. With no previous state (first manage, or after a reset) the file is not the app's
    /// to compare, and the order it was seeded with has to win.
    /// </summary>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        if (!syncTree.TryGetValue(Path, out var node) || !node.HavePrevious) return;

        var variety = Variety;
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) return;

        string text;
        using (var reader = new StreamReader(stream, leaveOpen: true))
            text = await reader.ReadToEndAsync();

        var installation = loadout.InstallationInstance;
        var expected = await Expected(installation, _game.EarlyLoadingPlugins(installation), variety.StoredOrder(loadout), syncTree);
        var difference = PluginListDifference.Between(expected, _mechanism.Parse(text));

        PluginListNotices.Stage(loadout.Db, found.Value, difference.IsEmpty ? null : difference.Summary(), DateTimeOffset.UtcNow, tx);
    }
}
