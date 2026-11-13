using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Larian.Engine.GameInstalled;
using SilverModManager.Games.Larian.Engine.ModList;
using SilverModManager.Games.Larian.Engine.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Larian.Engine.Modsettings;

/// <summary>
/// The game's mod list, <c>modsettings.lsx</c>. The paks in it, and their order, come from the loadout's pak sort
/// order as <see cref="LarianSynchronizer"/> worked them out before the sync (<see cref="LarianModListNotices"/>
/// holds the result); the game's own modules and the file's version are kept from the file on disk. This only
/// renders and compares, it never changes the loadout's order: the same sync that finds the game's edits writes
/// the app's list over them.
/// </summary>
public sealed class ModsettingsFile : IIntrinsicFileWithReset
{
    private readonly ILarianGame _game;
    private readonly LarianPakModules _modules;
    private readonly IModsettingsSchema _schema;
    private readonly ILogger<ModsettingsFile> _logger;

    public ModsettingsFile(ILarianGame game, LarianPakModules modules, IModsettingsSchema schema, GamePath path, ILogger<ModsettingsFile> logger)
    {
        _game = game;
        _modules = modules;
        _schema = schema;
        Path = path;
        _logger = logger;
    }

    public GamePath Path { get; }

    private ILarianPakSortOrderVariety Variety =>
        _game.SortOrderManager.GetSortOrderVarieties().OfType<ILarianPakSortOrderVariety>().Single();

    /// <inheritdoc />
    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var variety = Variety;
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db).Value;
        var candidates = await _modules.Candidates(loadout, variety, sortOrderId);
        var byName = ByName(candidates);

        var disk = ReadDocument(loadout);
        if (disk is null && !_schema.CanCreateFromNothing) throw new LarianModListMissingException(_game.Profile.GameName);

        // A campaign is in the game's list already and is kept there, so it is not one of the paks written after it.
        var pakUuids = UuidsOf(AddOns(candidates));
        var baseEntries = disk is null
            ? _schema.EntriesWhenNoFile
            : _schema.GameEntries(disk, AdventureUuids(candidates)).Where(e => !pakUuids.Contains(e.Uuid)).ToList();

        var pakEntries = LarianModListNotices.LastWritten(loadout.Db, sortOrderId)
            .Select(name => byName.GetValueOrDefault(name.ToLowerInvariant())?.Entry)
            .OfType<ModsettingsDocument.ModuleEntry>();

        // While the game's list is held, the file is written back as it is.
        IEnumerable<ModsettingsDocument.ModuleEntry> entries = disk is not null && LarianModListNotices.IsHeld(loadout.Db, sortOrderId)
            ? disk.Entries
            : baseEntries.Concat(pakEntries);

        var version = VersionOf(disk);
        var text = _schema.Render(version, entries, disk);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
    }

    /// <inheritdoc />
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        if (!syncTree.TryGetValue(Path, out var node) || !node.HavePrevious) return;

        var variety = Variety;
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found) return;
        var sortOrderId = found.Value;

        // The file is the game's while the list is held, so there is no app list to compare it with.
        if (LarianModListNotices.IsHeld(loadout.Db, sortOrderId)) return;

        ModsettingsDocument document;
        try
        {
            // Not detecting the byte order mark leaves it in the text, where the document notes it.
            using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            document = _schema.Parse(await reader.ReadToEndAsync());
        }
        catch (Exception e) when (e is XmlException or InvalidDataException)
        {
            // The write that follows replaces a file the game cannot have meant to leave like this.
            _logger.LogWarning(e, "modsettings.lsx cannot be read, it is replaced without a comparison");
            return;
        }

        var candidates = await _modules.Candidates(loadout, variety, sortOrderId);
        var lastWritten = LarianModListNotices.LastWritten(loadout.Db, sortOrderId);

        // A list the game threw away was already reported while the sync was prepared, with the paks that
        // went into it; describing the same file again here would replace that note.
        if (GameThrewTheListAway(loadout, candidates, lastWritten)) return;

        var byName = ByName(candidates);

        var expected = lastWritten
            .Select(name => byName.GetValueOrDefault(name.ToLowerInvariant()))
            .OfType<LarianPakCandidate>()
            .ToList();

        // The paks that were written come first, so an entry that two paks provide is taken for the one written.
        var byUuid = ByUuid(expected.Concat(AddOns(candidates)));

        // Base entries and modules the loadout has no pak for are the game's business, not part of the comparison.
        var onDisk = new List<LarianPakCandidate>();
        foreach (var entry in document.Entries)
        {
            if (byUuid.TryGetValue(entry.Uuid, out var pak) && !onDisk.Contains(pak)) onDisk.Add(pak);
        }

        var summary = Describe(expected, onDisk);
        LarianModListNotices.StageSummary(loadout.Db, sortOrderId, summary, DateTimeOffset.UtcNow, tx);
    }

    /// <inheritdoc />
    public async Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState)
    {
        var disk = ReadDocument(lastSyncedLoadout);
        if (disk is null) return null;

        var variety = Variety;
        if (variety.GetSortOrderIdFor(lastSyncedLoadout.LoadoutId, lastSyncedLoadout.Db) is not { HasValue: true } found) return null;
        var candidates = await _modules.Candidates(lastSyncedLoadout, variety, found.Value);

        // A pak the reset leaves behind (the game's own, or one nobody deployed) keeps its entry; the app's paks
        // go off disk, and the game would refuse to start a list that names them.
        bool StaysOnDisk(LarianPakCandidate pak) =>
            desiredState.TryGetValue(new GamePath(LarianConstants.ModsLocationId, pak.Name), out var node) && node.HaveLoadout;

        var removed = candidates
            .Where(p => !p.IsGameInstalled)
            .Where(p => !StaysOnDisk(p))
            .Select(p => p.Entry?.Uuid)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = disk.Entries.Where(e => !removed.Contains(e.Uuid)).ToList();

        // The reset hands the game back its own paks whatever the group's switch said: a pak whose group was off
        // has no entry in the file the app wrote, and the game would not list it after the app is gone. One the
        // player deleted since the last sync stays out, for the same reason the app's removed paks do.
        var listed = kept.Select(e => e.Uuid).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pak in candidates.Where(p => p.IsGameInstalled && StaysOnDisk(p)))
        {
            if (pak.Entry is { } entry && listed.Add(entry.Uuid)) kept.Add(entry);
        }

        var text = _schema.Render(VersionOf(disk), kept, disk);
        return new UTF8Encoding(false).GetBytes(text);
    }

    /// <summary>
    /// Whether the game threw away the list the app wrote: the file holds only base modules, with no entry for any
    /// pak and none from the game's mod downloads, while paks the app wrote last are still on disk. A file that
    /// still names a pak is the game changing the list, which the notice reports, and a list with nothing on disk
    /// to name is not one the game could have thrown away.
    /// </summary>
    public bool GameThrewTheListAway(Loadout.ReadOnly loadout, IReadOnlyList<LarianPakCandidate> candidates, IReadOnlyList<string> lastWritten)
    {
        var byName = ByName(candidates);
        var writtenAndOnDisk = lastWritten
            .Select(name => byName.GetValueOrDefault(name.ToLowerInvariant()))
            .OfType<LarianPakCandidate>()
            .Where(pak => pak.Entry is not null)
            .Where(pak => loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(LarianConstants.ModsLocationId, pak.Name)).FileExists)
            .ToList();
        if (writtenAndOnDisk.Count == 0) return false;

        if (ReadDocument(loadout) is not { } disk) return false;
        var pakUuids = UuidsOf(AddOns(candidates));
        var gameEntries = _schema.GameEntries(disk, AdventureUuids(candidates)).ToHashSet();
        return !disk.Entries.Any(e => !gameEntries.Contains(e) || pakUuids.Contains(e.Uuid));
    }

    /// <summary>The paks of the loadout that the file on disk lists, in the file's order.</summary>
    public IReadOnlyList<string> PaksListedOnDisk(Loadout.ReadOnly loadout, IReadOnlyList<LarianPakCandidate> candidates)
    {
        if (ReadDocument(loadout) is not { } disk) return [];

        var byUuid = ByUuid(AddOns(candidates));
        var names = new List<string>();
        foreach (var entry in disk.Entries)
        {
            if (byUuid.TryGetValue(entry.Uuid, out var pak) && !names.Contains(pak.Name)) names.Add(pak.Name);
        }
        return names;
    }

    /// <summary>What the game changed, in a sentence, or null when the paks and their order are as the app wrote them.</summary>
    private static string? Describe(IReadOnlyList<LarianPakCandidate> expected, IReadOnlyList<LarianPakCandidate> onDisk)
    {
        var removed = expected.Where(p => !onDisk.Contains(p)).Select(p => p.Name).ToList();
        var added = onDisk.Where(p => !expected.Contains(p)).Select(p => p.Name).ToList();
        var reordered = !expected.Where(onDisk.Contains).SequenceEqual(onDisk.Where(expected.Contains));

        var parts = new List<string>();
        if (removed.Count > 0) parts.Add($"removed {string.Join(", ", removed)}");
        if (added.Count > 0) parts.Add($"added {string.Join(", ", added)}");
        if (reordered) parts.Add("changed the load order");
        return parts.Count == 0 ? null : $"The game changed the mod list ({string.Join("; ", parts)}).";
    }

    /// <summary>The file as the game left it, or null when there is none or it cannot be read.</summary>
    public ModsettingsDocument? ReadDocument(Loadout.ReadOnly loadout)
    {
        var file = loadout.InstallationInstance.Locations.ToAbsolutePath(Path);
        if (!file.FileExists) return null;

        try
        {
            using var reader = new StreamReader(file.ToString(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            return _schema.Parse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "modsettings.lsx cannot be read, the game's own modules fall back to their defaults");
            return null;
        }
    }

    private IReadOnlyDictionary<string, string> VersionOf(ModsettingsDocument? disk) =>
        disk is { VersionAttributes.Count: > 0 } ? disk.VersionAttributes : _schema.DefaultVersion;

    /// <summary>The paks that are not campaigns: the ones the app orders and writes.</summary>
    private static IEnumerable<LarianPakCandidate> AddOns(IEnumerable<LarianPakCandidate> candidates) => candidates.Where(p => !p.IsAdventure);

    private static HashSet<string> AdventureUuids(IEnumerable<LarianPakCandidate> candidates) =>
        candidates.Where(p => p.IsAdventure).Select(p => p.Entry?.Uuid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, LarianPakCandidate> ByName(IEnumerable<LarianPakCandidate> candidates) =>
        candidates.ToDictionary(p => p.Name.ToLowerInvariant());

    /// <summary>The pak each module comes from; the first one in order when several provide it.</summary>
    private static Dictionary<string, LarianPakCandidate> ByUuid(IEnumerable<LarianPakCandidate> candidates)
    {
        var byUuid = new Dictionary<string, LarianPakCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var pak in candidates)
        {
            if (pak.Entry is not null) byUuid.TryAdd(pak.Entry.Uuid, pak);
        }
        return byUuid;
    }

    private static HashSet<string> UuidsOf(IEnumerable<LarianPakCandidate> candidates) =>
        candidates.Select(p => p.Entry?.Uuid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
}
