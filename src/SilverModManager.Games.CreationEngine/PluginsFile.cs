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
using NexusMods.Games.CreationEngine.Creations;
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
/// A game can have several such files (plugins.txt beside loadorder.txt), each in its own <see cref="IPluginListCodec"/>.
/// A codec that rewrites an existing file (Morrowind.ini, openmw.cfg) gets the current file to work on.
/// </summary>
public class PluginsFile : IIntrinsicFileWithReset
{
    private readonly ICreationEngineGame _game;
    private readonly IPluginListCodec _codec;
    private readonly bool _comparesWithGame;
    private readonly PluginListNoticeGroup _noticeGroup;
    private readonly ILogger<PluginsFile> _logger;

    private record struct Metadata(RelativePath FileName, Hash Hash, PluginHeader Header);

    /// <param name="path">Where the file lives; the game's plugins file when omitted.</param>
    /// <param name="noticeGroup">The files whose differences make up one notice; a file that is not given one reports alone.</param>
    /// <param name="comparesWithGame">
    /// Whether Ingest compares the game's copy and records what differs. False for a file that holds more than the
    /// list (Morrowind.ini) or that has no notice of its own.
    /// </param>
    public PluginsFile(ILogger<PluginsFile> logger, ICreationEngineGame game, IPluginListCodec codec, GamePath? path = null, PluginListNoticeGroup? noticeGroup = null, bool comparesWithGame = true)
    {
        _game = game;
        _codec = codec;
        _comparesWithGame = comparesWithGame;
        _noticeGroup = noticeGroup ?? new PluginListNoticeGroup();
        if (comparesWithGame) _noticeGroup.Add(this);
        Path = path ?? game.PluginsFile;
        _logger = logger;
    }

    public GamePath Path { get; }

    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var installation = loadout.InstallationInstance;
        await Render(stream, installation, _game.EarlyLoadingPlugins(installation), Variety.StoredOrder(loadout), syncTree, InstalledByGame(loadout));
    }

    /// <summary>
    /// After a reset the game is handed back to itself, so the Creations it installed are written active whatever
    /// the group's switch said: a plugin left unstarred would not load, and nothing in the app would be left to say why.
    /// </summary>
    public async Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState)
    {
        var installed = InstalledByGame(lastSyncedLoadout) with { GroupEnabled = true };

        // With no Creations to keep active there is nothing to say: leave the file to the reset's usual handling,
        // which puts back the one the user had or removes the one the app wrote.
        var keepsACreation = desiredState.Any(e => e.Value.HaveLoadout && IsPluginPath(e.Key) && installed.Contains(e.Key.FileName));
        if (!keepsACreation) return null;

        var installation = lastSyncedLoadout.InstallationInstance;
        var (bom, bytes) = await RenderBytes(installation, _game.EarlyLoadingPlugins(installation), Variety.StoredOrder(lastSyncedLoadout), desiredState, installed);
        return [.. bom, .. bytes];
    }

    private GameInstalledPlugins InstalledByGame(Loadout.ReadOnly loadout) => GameInstalledPlugins.For(loadout.Db, loadout.LoadoutId, _game.DataPath);

    /// <summary>The names of every plugin the file would list, in load order.</summary>
    internal async Task<IReadOnlyList<string>> LoadOrder(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree)
    {
        var installation = loadout.InstallationInstance;
        var listed = await Expected(installation, _game.EarlyLoadingPlugins(installation), Variety.StoredOrder(loadout), syncTree, InstalledByGame(loadout));
        return listed.Select(e => e.Name).ToList();
    }

    private IPluginSortOrderVariety Variety => _game.SortOrderManager.GetSortOrderVarieties().OfType<IPluginSortOrderVariety>().Single();

    /// <param name="earlyLoaders">Plugins the game loads before any list, in load order.</param>
    /// <param name="rows">The stored order and switches; a plugin without a row is on and goes where the resolver puts it.</param>
    /// <param name="gameInstalled">The plugins the game installed itself; none when omitted.</param>
    internal async Task Render(
        Stream stream,
        GameInstallation installation,
        IReadOnlyList<string> earlyLoaders,
        IReadOnlyList<PluginSortData> rows,
        IReadOnlyDictionary<GamePath, SyncNode> syncTree,
        GameInstalledPlugins? gameInstalled = null)
    {
        var (bom, bytes) = await RenderBytes(installation, earlyLoaders, rows, syncTree, gameInstalled ?? GameInstalledPlugins.None);
        await stream.WriteAsync(bom);
        await stream.WriteAsync(bytes);
    }

    private async Task<(byte[] Bom, byte[] Bytes)> RenderBytes(
        GameInstallation installation,
        IReadOnlyList<string> earlyLoaders,
        IReadOnlyList<PluginSortData> rows,
        IReadOnlyDictionary<GamePath, SyncNode> syncTree,
        GameInstalledPlugins gameInstalled)
    {
        var listed = await Expected(installation, earlyLoaders, rows, syncTree, gameInstalled);
        var (existing, bom) = _codec.RewritesExisting ? await ReadExisting(installation) : ("", []);

        var dataPlugins = syncTree.Keys.Where(IsPluginPath).Select(path => path.FileName.ToString()).ToHashSet(PluginNameComparer.Instance);
        var text = _codec.Render(listed, new PluginListTarget(existing, dataPlugins));

        return (bom, Encode(text, listed));
    }

    /// <summary>The file's bytes. A name the encoding can't hold is reported by name instead of written as a substitute.</summary>
    private byte[] Encode(string text, IEnumerable<PluginListEntry> listed)
    {
        try
        {
            return _codec.Encoding.GetBytes(text);
        }
        catch (EncoderFallbackException)
        {
            var offenders = listed
                .Select(e => e.Name)
                .Where(name => text.Contains(name, StringComparison.Ordinal) && !PluginListEncoding.CanEncode(_codec.Encoding, name))
                .Distinct(PluginNameComparer.Instance)
                .ToList();
            throw new UnencodablePluginNamesException(Path.FileName.ToString(), _codec.Encoding.WebName, offenders);
        }
    }

    private static readonly byte[] Utf8Bom = Encoding.UTF8.GetPreamble();

    /// <summary>The text of the file now on disk, and the byte order mark it started with so a rewrite can keep it.</summary>
    private async Task<(string Text, byte[] Bom)> ReadExisting(GameInstallation installation)
    {
        var onDisk = installation.Locations.ToAbsolutePath(Path);
        if (!onDisk.FileExists) return ("", []);

        var bytes = await onDisk.ReadAllBytesAsync();
        var hasBom = bytes.AsSpan().StartsWith(Utf8Bom);
        return (_codec.Decode(bytes.AsSpan(hasBom ? Utf8Bom.Length : 0)), hasBom ? Utf8Bom : []);
    }

    /// <summary>
    /// A file that is on disk but not yet in the loadout, which this sync takes in (a plugin that was in the Data
    /// folder before the game was managed). It is listed now so the first list does not switch it off.
    /// </summary>
    private static bool IsAdoptedFromDisk(SyncNode node) =>
        !node.HaveLoadout && node.HaveDisk && node.Actions.HasFlag(Actions.IngestFromDisk);

    private bool IsPluginPath(GamePath path) =>
        path.Parent == _game.DataPath && (KnownCEExtensions.PluginFiles.Contains(path.Extension) || _game.PluginExtensions.Contains(path.Extension));

    /// <summary>
    /// The plugin list the app wants on disk: every plugin of the sync tree in load order, with its switch.
    /// Render writes this and Ingest compares the game's file with it, so both see the same list.
    /// </summary>
    private async Task<List<PluginListEntry>> Expected(
        GameInstallation installation,
        IReadOnlyList<string> earlyLoaders,
        IReadOnlyList<PluginSortData> rows,
        IReadOnlyDictionary<GamePath, SyncNode> syncTree,
        GameInstalledPlugins gameInstalled)
    {
        var plugins = new List<Metadata>();
        List<string> unreadable = [];
        List<string> unreadableInstalled = [];
        foreach (var entry in syncTree)
        {
            if (!IsPluginPath(entry.Key)) continue;
            var adopted = IsAdoptedFromDisk(entry.Value);
            if (!entry.Value.HaveLoadout && !adopted) continue;

            if (await MakeMetadata(entry) is { } found)
                plugins.Add(found);
            // A file the sync is about to take into the loadout may not be readable before it is stored; it is
            // listed on the next Apply instead of blocking this one.
            else if (adopted)
                _logger.LogDebug("The header of {Plugin}, already in the Data folder, could not be read yet", entry.Key.FileName);
            else if (gameInstalled.Contains(entry.Key.FileName))
                unreadableInstalled.Add(entry.Key.FileName);
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

        // The game switches these on whatever the list and the user's switches say.
        var implicitlyActive = _game.ImplicitlyActivePlugins(
                syncTree.Where(e => e.Key.Parent == _game.DataPath && (e.Value.HaveLoadout || IsAdoptedFromDisk(e.Value))).Select(e => e.Key.FileName.ToString()))
            .ToHashSet(PluginNameComparer.Instance);

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

        // A Creation the game is still downloading has no readable header yet. It must not block every Apply, so it
        // is listed where its row puts it, as a plugin with no masters.
        foreach (var name in unreadableInstalled)
        {
            _logger.LogWarning("The header of {Plugin}, installed by the game, could not be read; listing it without masters", name);
            entries.Add(new PluginOrderEntry(
                name, false, false, [],
                rowsByName.TryGetValue(name, out var row) ? row.SortIndex : null,
                early.TryGetValue(name, out var earlyIndex) ? earlyIndex : null));
        }

        var order = PluginOrderResolver.Resolve(entries);
        Debug.Assert(PluginOrderValidation.Validate(order, entries, earlyLoaders) is null,
            "The resolver produced a load order that PluginOrderValidation rejects.");
        _logger.LogDebug("Ordered {Count} plugin files", order.Count);

        var byName = entries.ToDictionary(e => e.Name, PluginNameComparer.Instance);
        return order.Select(name =>
        {
            var entry = byName[name];
            // A blueprint master never loads early, so the game does not know to load it without the list.
            var isEarly = entry.LoadsEarly;
            var installed = gameInstalled.Contains(name);
            if (installed && !gameInstalled.GroupEnabled && isEarly && !implicitlyActive.Contains(name))
            {
                // Every Creation on a typical Anniversary install is listed in the .ccc file, and the asterisk format
                // leaves early loaders out, so without an explicit line the group's switch would not change the file.
                // Still an early loader so the game rewriting the line away is not reported as an edit on every Apply.
                return new PluginListEntry(name, false, IsEarlyLoader: true, RenderExplicitly: true);
            }

            // The group's switch alone decides a plugin the game installed; its own row is ignored.
            var active = isEarly || implicitlyActive.Contains(name)
                || (installed ? gameInstalled.GroupEnabled : !rowsByName.TryGetValue(name, out var row) || row.IsEnabled);
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
    /// Compares the game's files with the list the app would write and records what differs as a notice. The order
    /// and switches in the database are never changed from here: the same Apply rewrites the files from them, which
    /// replaces the game's edits. With no previous state (first manage, or after a reset) the file is not the app's
    /// to compare, and the order it was seeded with has to win.
    /// </summary>
    /// <remarks>
    /// The notice covers every file of the group the game changed, whichever of them is ingested first, so a sibling
    /// the game also changed is read from disk here. A sibling the game left alone is skipped: it still holds what
    /// the app last wrote, so all it could differ by is a change the user made in the app since.
    /// </remarks>
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        if (!_comparesWithGame) return;
        if (!syncTree.TryGetValue(Path, out var node) || !node.HavePrevious) return;

        var variety = Variety;
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) return;

        string text;
        using (var buffer = new MemoryStream())
        {
            await stream.CopyToAsync(buffer);
            text = _codec.Decode(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        }

        var installation = loadout.InstallationInstance;
        var expected = await Expected(installation, _game.EarlyLoadingPlugins(installation), variety.StoredOrder(loadout), syncTree, InstalledByGame(loadout));

        var differences = new List<PluginListDifference>();
        foreach (var file in _noticeGroup.Files)
        {
            var fileText = ReferenceEquals(file, this) ? text : await file.TextOnDisk(installation, syncTree);
            if (fileText is null) continue;
            differences.Add(PluginListDifference.Between(expected, file._codec.Parse(fileText), file._codec.CarriesSwitches));
        }

        if (!_noticeGroup.TryClaim(tx)) return;
        var difference = PluginListDifference.Combine(differences.ToArray());
        PluginListNotices.Stage(loadout.Db, found.Value, difference.IsEmpty ? null : difference.Summary(), DateTimeOffset.UtcNow, tx);
    }

    /// <summary>The file as the game left it, or null when the game did not change it since the app last wrote it.</summary>
    private async Task<string?> TextOnDisk(GameInstallation installation, IReadOnlyDictionary<GamePath, SyncNode> syncTree)
    {
        if (!syncTree.TryGetValue(Path, out var node) || !node.HavePrevious || !node.Actions.HasFlag(Actions.AdaptLoadout)) return null;
        var file = installation.Locations.ToAbsolutePath(Path);
        return file.FileExists ? _codec.Decode(await file.ReadAllBytesAsync()) : null;
    }
}
