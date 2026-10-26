using System.Text.RegularExpressions;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>One tweak file a mod, or the loadout itself, supplies for an INI.</summary>
/// <param name="Name">The part of the file name before the bracketed target.</param>
/// <param name="Hash">The tweak's bytes in the file store.</param>
public readonly record struct IniTweakLayer(string Name, Hash Hash);

/// <summary>
/// Finds the tweaks that apply to one INI: <c>INI Tweaks/&lt;Name&gt; [&lt;Target.ini&gt;].ini</c> files in the
/// loadout. A mod's tweak counts while the mod is enabled; the loadout's own tweak, <c>INI Tweaks/Loadout
/// [&lt;Target.ini&gt;].ini</c> in the game settings group, comes last.
/// </summary>
public static partial class IniTweakLayers
{
    /// <summary>The name of the loadout's own tweak.</summary>
    public const string LoadoutTweakName = "Loadout";

    private const string Folder = "INI Tweaks";

    [GeneratedRegex(@"^(?<name>.+) \[(?<target>[^\]]+)\]\.ini$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TweakName();

    /// <summary>
    /// The tweaks for <paramref name="target"/>, in the order they are merged: mods by priority, lowest first so
    /// the highest wins, then the loadout's own tweak.
    /// </summary>
    public static IReadOnlyList<IniTweakLayer> For(Loadout.ReadOnly loadout, GamePath target)
    {
        var priorities = new Dictionary<EntityId, ConflictPriority>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
            priorities[row.TargetId] = row.Priority;

        var found = new List<(ConflictPriority Priority, bool IsLoadoutTweak, string Path, IniTweakLayer Layer)>();
        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var item = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
            if (!item.IsEnabled()) continue;

            var path = ((GamePath)file.AsLoadoutItemWithTargetPath().TargetPath).Path;
            if (!TryParse(path, target.FileName.ToString(), out var name)) continue;

            var isLoadoutTweak = name.Equals(LoadoutTweakName, StringComparison.OrdinalIgnoreCase) && InGameSettingsGroup(item);
            found.Add((PriorityOf(item, priorities), isLoadoutTweak, path.ToString(), new IniTweakLayer(name, file.Hash)));
        }

        return found
            .OrderBy(f => f.IsLoadoutTweak)
            .ThenBy(f => f.Priority.Value)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f.Layer)
            .ToList();
    }

    private static bool TryParse(NexusMods.Paths.RelativePath path, string targetFileName, out string name)
    {
        name = "";
        var parts = path.Path.Split('/');
        if (parts.Length != 2 || !parts[0].Equals(Folder, StringComparison.OrdinalIgnoreCase)) return false;

        var match = TweakName().Match(path.FileName.ToString());
        if (!match.Success || !match.Groups["target"].Value.Equals(targetFileName, StringComparison.OrdinalIgnoreCase)) return false;

        name = match.Groups["name"].Value;
        return true;
    }

    private static bool InGameSettingsGroup(LoadoutItem.ReadOnly item) =>
        item.GetThisAndParents().Any(i => i.Contains(LoadoutItemGroup.GameSettingsGroup));

    // The nearest group above the file that has a priority row: the mod the file belongs to.
    private static ConflictPriority PriorityOf(LoadoutItem.ReadOnly item, Dictionary<EntityId, ConflictPriority> priorities)
    {
        foreach (var ancestor in item.GetThisAndParents())
            if (priorities.TryGetValue(ancestor.Id, out var priority)) return priority;
        return ConflictPriority.MinValue;
    }
}
