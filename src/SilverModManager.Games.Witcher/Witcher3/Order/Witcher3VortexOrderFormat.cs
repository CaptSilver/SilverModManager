using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SilverModManager.Abstractions.Collections.Json;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.Sdk.Library;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.Witcher.Witcher3.Order;

/// <summary>
/// The <c>loadOrder</c> key of a Vortex collection for The Witcher 3: a list of
/// <c>{ id, name, enabled, modId, locked, data: { prefix } }</c> entries, one per Mods folder. <c>id</c> is the folder
/// (Vortex matches it against mods.settings), <c>modId</c> the name of the archive the folder came from, and
/// <c>data.prefix</c> the folder's mods.settings priority. Older Vortex versions stored an object keyed by folder; that
/// form is read too.
/// </summary>
public sealed class Witcher3VortexOrderFormat : IVortexOrderFormat
{
    private const string LoadOrderPage = "Load Order";
    private const string LockedPrefix = "mod0000_";

    /// <summary>Vortex names an install <c>name-modId-version-timestamp</c>, so the first number between dashes is the Nexus mod id.</summary>
    private static readonly Regex ModIdInName = new(@"-(?<modId>\d+)-", RegexOptions.CultureInvariant);

    public string Key => "loadOrder";

    /// <summary>
    /// Vortex's importer destructures <c>mergedData</c> before it reads the order, and skips each field that is absent, so an
    /// empty object imports the order and nothing else. The app does not export merged script or menu data.
    /// </summary>
    public JsonElement? MergedData { get; } = JsonSerializer.SerializeToElement(new JsonObject());

    public (IReadOnlyList<ModOrderSourceEntry> Entries, IReadOnlyList<ModOrderDrop> Unmapped) Read(JsonElement value, CollectionRoot root)
    {
        var drops = new List<ModOrderDrop>();
        var listed = Parse(value, drops)
            .Where(entry => !entry.Locked && !entry.Id.StartsWith(LockedPrefix, StringComparison.OrdinalIgnoreCase))
            // OrderBy is stable, so equal or missing prefixes keep the order they were listed in.
            .OrderBy(entry => entry.Prefix ?? double.MaxValue)
            .ToArray();

        var joiner = new ModJoiner(root.Mods);
        var entries = new List<ModOrderSourceEntry>();
        var placed = new HashSet<int>();
        foreach (var entry in listed)
        {
            if (joiner.Find(entry.ModId) is not { } index)
            {
                drops.Add(new ModOrderDrop(entry.Name, $"no mod in the collection matches '{entry.ModId}'", LoadOrderPage));
                continue;
            }

            // A mod with several folders is listed once per folder; it is one mod in the order.
            if (!placed.Add(index)) continue;
            entries.Add(new ModOrderSourceEntry(ReferenceOf(root.Mods[index]), entry.Enabled));
        }

        return (entries, drops);
    }

    public JsonElement? Write(Loadout.ReadOnly loadout, IReadOnlyList<LoadoutItemGroup.ReadOnly> winnerToLoser)
    {
        var rows = new List<(string Folder, string Name, bool Enabled, string ModId)>();
        var seen = new HashSet<SortItemKey<string>> { WitcherModFolders.MergedKey };
        foreach (var group in winnerToLoser)
        {
            var item = group.AsLoadoutItem();
            var archive = ArchiveName(group);
            foreach (var folder in FoldersOf(group))
            {
                if (!seen.Add(WitcherModFolders.KeyFor(folder))) continue;

                var name = folder.Equals(item.Name, StringComparison.OrdinalIgnoreCase) ? folder : $"{item.Name} ({folder})";
                rows.Add((folder, name, !item.IsDisabled, archive ?? folder));
            }
        }

        if (rows.Count == 0) return null;

        var merged = WitcherConstants.W3MergedFolder;
        var array = new JsonArray { Entry(merged, merged, true, merged, locked: true, prefix: 1) };
        for (var i = 0; i < rows.Count; i++)
        {
            var (folder, name, enabled, modId) = rows[i];
            array.Add(Entry(folder, name, enabled, modId, locked: false, prefix: i + 2));
        }

        return JsonSerializer.SerializeToElement(array);
    }

    private static JsonObject Entry(string id, string name, bool enabled, string modId, bool locked, int prefix) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["enabled"] = enabled,
        ["modId"] = modId,
        ["locked"] = locked,
        ["data"] = new JsonObject { ["prefix"] = prefix },
    };

    /// <summary>The Mods folders a group puts files in, in path order.</summary>
    private static IEnumerable<string> FoldersOf(LoadoutItemGroup.ReadOnly group)
    {
        var folders = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Collect(group, folders);
        return folders.Values;

        static void Collect(LoadoutItemGroup.ReadOnly group, SortedDictionary<string, string> folders)
        {
            foreach (var file in group.Children.OfTypeLoadoutItemWithTargetPath())
            {
                if (WitcherModFolders.FolderOf(file.TargetPath) is { } folder)
                    folders.TryAdd(folder, folder);
            }

            foreach (var child in group.Children.OfTypeLoadoutItemGroup())
                Collect(child, folders);
        }
    }

    /// <summary>The file name of the download the group was installed from, without its extension: Vortex's install id for the mod.</summary>
    private static string? ArchiveName(LoadoutItemGroup.ReadOnly group)
    {
        if (!group.TryGetAsLibraryLinkedLoadoutItem(out var linked)) return null;
        return linked.LibraryItem.TryGetAsLibraryFile(out var file) ? Path.GetFileNameWithoutExtension(file.FileName) : null;
    }

    private static ModOrderReference ReferenceOf(Mod mod)
    {
        var source = mod.Source;
        var isNexus = source.Type == ModSourceType.NexusMods && source.ModId.Value != 0 && source.FileId.Value != 0;
        return new ModOrderReference(
            source.Md5,
            isNexus ? source.ModId : null,
            isNexus ? source.FileId : null,
            string.IsNullOrWhiteSpace(mod.Name) ? null : mod.Name);
    }

    private sealed record ListedEntry(string Id, string Name, bool Enabled, string ModId, bool Locked, double? Prefix);

    private static IEnumerable<ListedEntry> Parse(JsonElement value, List<ModOrderDrop> drops)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object && ParseEntry(element, null) is { } entry) yield return entry;
                    else drops.Add(new ModOrderDrop("an entry of loadOrder", "it has no id", LoadOrderPage));
                }
                break;
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && ParseEntry(property.Value, property.Name) is { } entry) yield return entry;
                    else drops.Add(new ModOrderDrop(property.Name, "its loadOrder value is not an object", LoadOrderPage));
                }
                break;
            default:
                drops.Add(new ModOrderDrop("loadOrder", "it is neither a list nor an object of mods", LoadOrderPage));
                break;
        }
    }

    private static ListedEntry? ParseEntry(JsonElement element, string? keyId)
    {
        var id = keyId ?? Text(element, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var enabled = !(element.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
        var locked = element.TryGetProperty("locked", out var l) && l.ValueKind == JsonValueKind.True;
        var prefix = element.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("prefix", out var p)
            ? Number(p)
            : element.TryGetProperty("prefix", out var legacy) ? Number(legacy) : null;
        return new ListedEntry(id, Text(element, "name") is { Length: > 0 } name ? name : id, enabled, Text(element, "modId") is { Length: > 0 } modId ? modId : id, locked, prefix);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Vortex has written the prefix both as a number and as a string.</summary>
    private static double? Number(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null,
    };

    /// <summary>Finds the mods[] entry a Vortex install id belongs to.</summary>
    private sealed class ModJoiner(Mod[] mods)
    {
        /// <summary>
        /// By the mod's logical file name, else its file expression (with or without the extension), else its name when
        /// exactly one mod has it, else the Nexus mod id inside the install id when exactly one mod has it. Null when none
        /// names a single mod.
        /// </summary>
        public int? Find(string modId)
        {
            for (var i = 0; i < mods.Length; i++)
            {
                if (Same(mods[i].Source.LogicalFilename, modId)) return i;
            }

            for (var i = 0; i < mods.Length; i++)
            {
                var expression = mods[i].Source.FileExpression.Path;
                if (!string.IsNullOrEmpty(expression) && (Same(expression, modId) || Same(Path.GetFileNameWithoutExtension(expression), modId))) return i;
            }

            // A display name can be shared by two mods; it only counts when it names one.
            var named = Enumerable.Range(0, mods.Length).Where(i => Same(mods[i].Name, modId)).ToArray();
            if (named.Length == 1) return named[0];

            if (ModIdInName.Match(modId) is not { Success: true } match || !uint.TryParse(match.Groups["modId"].Value, out var nexusId))
                return null;

            int? found = null;
            for (var i = 0; i < mods.Length; i++)
            {
                if (mods[i].Source.Type != ModSourceType.NexusMods || mods[i].Source.ModId.Value != nexusId) continue;
                if (found is not null) return null;
                found = i;
            }

            return found;
        }

        private static bool Same(string? left, string right) => left is not null && left.Equals(right, StringComparison.OrdinalIgnoreCase);
    }
}
