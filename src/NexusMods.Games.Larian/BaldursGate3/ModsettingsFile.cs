using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Games.Larian.BaldursGate3.GameInstalled;
using NexusMods.Games.Larian.BaldursGate3.ModList;
using NexusMods.Games.Larian.BaldursGate3.SortOrder;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Loadouts;

namespace NexusMods.Games.Larian.BaldursGate3;

/// <summary>
/// The game's mod list, <c>modsettings.lsx</c>. The paks in it, and their order, come from the loadout's pak sort
/// order as <see cref="BaldursGate3Synchronizer"/> worked them out before the sync (<see cref="Bg3ModListNotices"/>
/// holds the result); the game's own modules and the file's version are kept from the file on disk. This only
/// renders and compares, it never changes the loadout's order: the same sync that finds the game's edits writes
/// the app's list over them.
/// </summary>
public sealed class ModsettingsFile : IIntrinsicFileWithReset
{
    public static readonly GamePath FilePath = new(Bg3Constants.PlayerProfilesLocationId, "modsettings.lsx");

    // The shape of a file the current game writes, used only when there is none on disk to keep.
    private static readonly IReadOnlyDictionary<string, string> DefaultVersion = new Dictionary<string, string>
    {
        ["major"] = "4", ["minor"] = "8", ["revision"] = "0", ["build"] = "400",
    };

    private static readonly IReadOnlyList<ModsettingsDocument.ModuleEntry> DefaultBaseEntries =
    [
        new("GustavX", "GustavX", "0", "cb555efe-2d9e-131f-8195-a89329d218ea", "145241302737902957", ""),
        new("HonourX", "HonourX", "0", "767d0062-d82c-279c-e16b-dfee7fe94cdd", "36028797025827000", ""),
    ];

    private readonly IGame _game;
    private readonly Bg3PakModules _modules;
    private readonly ILogger<ModsettingsFile> _logger;

    public ModsettingsFile(IGame game, Bg3PakModules modules, ILogger<ModsettingsFile> logger)
    {
        _game = game;
        _modules = modules;
        _logger = logger;
    }

    public GamePath Path => FilePath;

    private IBg3PakSortOrderVariety Variety =>
        _game.SortOrderManager.GetSortOrderVarieties().OfType<IBg3PakSortOrderVariety>().Single();

    /// <inheritdoc />
    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var variety = Variety;
        var sortOrderId = variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db).Value;
        var candidates = await _modules.Candidates(loadout, variety, sortOrderId);
        var byName = ByName(candidates);

        var disk = ReadDocument(loadout);
        var pakUuids = UuidsOf(candidates);
        var baseEntries = disk is null
            ? DefaultBaseEntries
            : disk.BaseEntries.Where(e => !pakUuids.Contains(e.Uuid)).ToList();

        var pakEntries = Bg3ModListNotices.LastWritten(loadout.Db, sortOrderId)
            .Select(name => byName.GetValueOrDefault(name.ToLowerInvariant())?.Entry)
            .OfType<ModsettingsDocument.ModuleEntry>();

        // While the game's list is held, the file is written back as it is.
        IEnumerable<ModsettingsDocument.ModuleEntry> entries = disk is not null && Bg3ModListNotices.IsHeld(loadout.Db, sortOrderId)
            ? disk.Entries
            : baseEntries.Concat(pakEntries);

        var version = VersionOf(disk);
        var text = ModsettingsDocument.Render(version, entries);
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
        if (Bg3ModListNotices.IsHeld(loadout.Db, sortOrderId)) return;

        ModsettingsDocument document;
        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            document = ModsettingsDocument.Parse(await reader.ReadToEndAsync());
        }
        catch (Exception e) when (e is XmlException or InvalidDataException)
        {
            // The write that follows replaces a file the game cannot have meant to leave like this.
            _logger.LogWarning(e, "modsettings.lsx cannot be read, it is replaced without a comparison");
            return;
        }

        var candidates = await _modules.Candidates(loadout, variety, sortOrderId);
        var byName = ByName(candidates);

        var expected = Bg3ModListNotices.LastWritten(loadout.Db, sortOrderId)
            .Select(name => byName.GetValueOrDefault(name.ToLowerInvariant()))
            .OfType<Bg3PakCandidate>()
            .ToList();

        // The paks that were written come first, so an entry that two paks provide is taken for the one written.
        var byUuid = ByUuid(expected.Concat(candidates));

        // Base entries and modules the loadout has no pak for are the game's business, not part of the comparison.
        var onDisk = new List<Bg3PakCandidate>();
        foreach (var entry in document.Entries)
        {
            if (byUuid.TryGetValue(entry.Uuid, out var pak) && !onDisk.Contains(pak)) onDisk.Add(pak);
        }

        var summary = Describe(expected, onDisk);
        Bg3ModListNotices.StageSummary(loadout.Db, sortOrderId, summary, DateTimeOffset.UtcNow, tx);
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
        var removed = candidates
            .Where(p => !p.IsGameInstalled)
            .Where(p => !desiredState.TryGetValue(new GamePath(Bg3Constants.ModsLocationId, p.Name), out var node) || !node.HaveLoadout)
            .Select(p => p.Entry?.Uuid)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var text = ModsettingsDocument.Render(VersionOf(disk), disk.Entries.Where(e => !removed.Contains(e.Uuid)));
        return new UTF8Encoding(false).GetBytes(text);
    }

    /// <summary>
    /// Whether the game threw away the list the app wrote: the file holds only base modules, with no entry for any
    /// pak and none from the game's mod downloads, while paks the app wrote last are still on disk. A file that
    /// still names a pak is the game changing the list, which the notice reports, and a list with nothing on disk
    /// to name is not one the game could have thrown away.
    /// </summary>
    public bool GameThrewTheListAway(Loadout.ReadOnly loadout, IReadOnlyList<Bg3PakCandidate> candidates, IReadOnlyList<string> lastWritten)
    {
        var byName = ByName(candidates);
        var writtenAndOnDisk = lastWritten
            .Select(name => byName.GetValueOrDefault(name.ToLowerInvariant()))
            .OfType<Bg3PakCandidate>()
            .Where(pak => pak.Entry is not null)
            .Where(pak => loadout.InstallationInstance.Locations.ToAbsolutePath(new GamePath(Bg3Constants.ModsLocationId, pak.Name)).FileExists)
            .ToList();
        if (writtenAndOnDisk.Count == 0) return false;

        if (ReadDocument(loadout) is not { } disk) return false;
        var pakUuids = UuidsOf(candidates);
        return !disk.Entries.Any(e => e.IsPublished || pakUuids.Contains(e.Uuid));
    }

    /// <summary>The paks of the loadout that the file on disk lists, in the file's order.</summary>
    public IReadOnlyList<string> PaksListedOnDisk(Loadout.ReadOnly loadout, IReadOnlyList<Bg3PakCandidate> candidates)
    {
        if (ReadDocument(loadout) is not { } disk) return [];

        var byUuid = ByUuid(candidates);
        var names = new List<string>();
        foreach (var entry in disk.Entries)
        {
            if (byUuid.TryGetValue(entry.Uuid, out var pak) && !names.Contains(pak.Name)) names.Add(pak.Name);
        }
        return names;
    }

    /// <summary>What the game changed, in a sentence, or null when the paks and their order are as the app wrote them.</summary>
    private static string? Describe(IReadOnlyList<Bg3PakCandidate> expected, IReadOnlyList<Bg3PakCandidate> onDisk)
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
            return ModsettingsDocument.Parse(File.ReadAllText(file.ToString()));
        }
        catch (Exception e) when (e is XmlException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "modsettings.lsx cannot be read, the game's own modules fall back to their defaults");
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> VersionOf(ModsettingsDocument? disk) =>
        disk is { VersionAttributes.Count: > 0 } ? disk.VersionAttributes : DefaultVersion;

    private static Dictionary<string, Bg3PakCandidate> ByName(IEnumerable<Bg3PakCandidate> candidates) =>
        candidates.ToDictionary(p => p.Name.ToLowerInvariant());

    /// <summary>The pak each module comes from; the first one in order when several provide it.</summary>
    private static Dictionary<string, Bg3PakCandidate> ByUuid(IEnumerable<Bg3PakCandidate> candidates)
    {
        var byUuid = new Dictionary<string, Bg3PakCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var pak in candidates)
        {
            if (pak.Entry is not null) byUuid.TryAdd(pak.Entry.Uuid, pak);
        }
        return byUuid;
    }

    private static HashSet<string> UuidsOf(IEnumerable<Bg3PakCandidate> candidates) =>
        candidates.Select(p => p.Entry?.Uuid).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
}
