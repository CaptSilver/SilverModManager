using System.Text;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3;

/// <summary>
/// <c>mods.settings</c> in Documents/The Witcher 3, rendered from the Load Order on every Apply. This only renders and
/// compares: a copy the game or Vortex wrote is recorded as a notice and the same Apply writes the app's over it.
/// </summary>
public sealed class ModsSettingsFile(IGame game, ILogger<ModsSettingsFile> logger) : IIntrinsicFileWithReset
{
    private const string UnreadableSummary = "the file on disk could not be read as mods.settings";

    public GamePath Path => WitcherConstants.W3ModsSettings;

    private IWitcherModSortOrderVariety Variety =>
        game.SortOrderManager.GetSortOrderVarieties().OfType<IWitcherModSortOrderVariety>().Single();

    /// <inheritdoc />
    public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
    {
        var text = ModsSettingsDocument.Render(Variety.StoredOrder(loadout));
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(text));
    }

    /// <inheritdoc />
    public async Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx)
    {
        // With no previous write the file is not the app's to compare: the first Apply renders over whatever is there.
        if (!syncTree.TryGetValue(Path, out var node) || !node.HavePrevious) return;

        var variety = Variety;
        if (variety.GetSortOrderIdFor(loadout.LoadoutId, loadout.Db) is not { HasValue: true } found)
        {
            logger.LogWarning("The Witcher 3 load order of loadout {Loadout} has no sort order to hold a note about mods.settings", loadout.LoadoutId);
            return;
        }

        string text;
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            text = await reader.ReadToEndAsync();

        string? summary;
        if (ModsSettingsDocument.CanParse(text))
        {
            summary = Describe(variety.StoredOrder(loadout), ModsSettingsDocument.Parse(text));
        }
        else
        {
            // The write that follows replaces a file the game cannot have meant to leave like this; say so anyway.
            logger.LogWarning("mods.settings cannot be read, it is replaced without a comparison");
            summary = UnreadableSummary;
        }

        ModsSettingsNotices.Stage(loadout.Db, found.Value, summary, DateTimeOffset.UtcNow, tx);
    }

    /// <inheritdoc />
    public async Task<byte[]?> RenderForReset(Loadout.ReadOnly lastSyncedLoadout, IReadOnlyDictionary<GamePath, SyncNode> desiredState)
    {
        var file = lastSyncedLoadout.InstallationInstance.Locations.ToAbsolutePath(Path);
        if (!file.FileExists) return null;

        var disk = ModsSettingsDocument.Parse(Encoding.UTF8.GetString(await file.ReadAllBytesAsync()));

        // A folder the app's files leave has no business in the file; one the game had before manage stays.
        var staying = desiredState
            .Where(entry => entry.Value.HaveLoadout)
            .Select(entry => WitcherModFolders.FolderOf(entry.Key))
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var kept = disk
            .Where(section => staying.Contains(section.Folder))
            .OrderBy(section => section.Priority)
            .Select(section => (section.Folder, section.Enabled))
            .ToList();
        return new UTF8Encoding(false).GetBytes(ModsSettingsDocument.Render(kept));
    }

    /// <summary>
    /// A sentence naming the folders on which <paramref name="onDisk"/> differs from <paramref name="expected"/>, or
    /// null when it says the same thing. Keys the app does not write (Vortex's <c>VK</c>) are not a difference.
    /// </summary>
    private static string? Describe(IReadOnlyList<(string Folder, bool IsEnabled)> expected, IReadOnlyList<ModsSettingsDocument.Section> onDisk)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var disk = onDisk.GroupBy(section => section.Folder, comparer).ToDictionary(g => g.Key, g => g.ToList(), comparer);
        var expectedFolders = expected.Select(e => e.Folder).ToHashSet(comparer);

        var missing = expected.Select(e => e.Folder).Where(folder => !disk.ContainsKey(folder)).ToList();
        var extra = disk.Keys.Where(folder => !expectedFolders.Contains(folder)).ToList();
        var switched = expected
            .Where(e => disk.TryGetValue(e.Folder, out var sections) && sections.Any(s => s.Enabled != e.IsEnabled))
            .Select(e => e.Folder)
            .ToList();

        // Folders the file lists twice, or with a Priority shared with another folder, have no order the app wrote.
        var shared = onDisk.GroupBy(section => section.Priority).Where(g => g.Count() > 1).SelectMany(g => g).Select(s => s.Folder);
        var ambiguous = disk.Where(entry => entry.Value.Count > 1).Select(entry => entry.Key).Concat(shared)
            .Where(expectedFolders.Contains).Distinct(comparer).ToList();

        // Priority order of the folders both sides have; ties keep file order.
        var common = expected.Select(e => e.Folder).Where(disk.ContainsKey).ToList();
        var diskOrder = onDisk
            .Select((section, index) => (section, index))
            .OrderBy(t => t.section.Priority).ThenBy(t => t.index)
            .Select(t => t.section.Folder)
            .Where(folder => expectedFolders.Contains(folder))
            .Distinct(comparer)
            .ToList();
        var reordered = common.Where(folder => diskOrder.IndexOf(folder) != common.IndexOf(folder)).ToList();

        var parts = new List<string>();
        if (reordered.Count > 0) parts.Add($"in a different order: {string.Join(", ", reordered)}");
        if (ambiguous.Count > 0) parts.Add($"without a place of their own: {string.Join(", ", ambiguous.Where(f => !reordered.Contains(f, comparer)))}");
        if (switched.Count > 0) parts.Add($"switched differently: {string.Join(", ", switched)}");
        if (extra.Count > 0) parts.Add($"not in your loadout: {string.Join(", ", extra)}");
        if (missing.Count > 0) parts.Add($"missing from the file: {string.Join(", ", missing)}");
        parts.RemoveAll(part => part.EndsWith(": ", StringComparison.Ordinal));

        return parts.Count == 0 ? null : $"the file on disk differed from your Load Order ({string.Join("; ", parts)})";
    }
}
