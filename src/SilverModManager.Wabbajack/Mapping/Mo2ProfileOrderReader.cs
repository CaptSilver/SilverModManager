using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.Games.LoadOrder;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Wabbajack.Mapping;

/// <summary>Reads an MO2 profile's modlist.txt as a <see cref="ModOrder"/> over the mod groups the install made.</summary>
public static class Mo2ProfileOrderReader
{
    /// <summary>
    /// The profile's order, winner first: Overwrite, then the listed mods top-down (the first line of modlist.txt wins),
    /// then mods with no line (alphabetical from the bottom up, so the last one is nearest the settings group; each is
    /// logged at Warning), then the game settings group, then the game-root groups. A '+' line that matches no group is
    /// dropped; a '-' line for a mod the list does not ship is one its author excluded on purpose, so it is not. A line
    /// named Overwrite does not move it: Overwrite is always first. <see cref="ModOrderEntry.Enabled"/> is false for a '-' line, true for a '+' line, and null for a mod with
    /// no line and for the settings group: modlist.txt never lists it, and a line matching its free-text name is about the mod.
    /// </summary>
    public static ModOrder Read(
        IReadOnlyList<LoadoutItemGroup.ReadOnly> children,
        IReadOnlyList<ModlistTxtEntry> entries,
        IReadOnlySet<string> gameRootGroupNames,
        string listName,
        ILogger logger)
    {
        // The settings group goes by its marker: its name is free text and may match a mod's.
        var settingsGroups = children.Where(c => c.IsGameSettingsGroup).ToArray();
        var mods = children.Where(c => !c.IsGameSettingsGroup).ToArray();
        var byName = mods.ToDictionary(c => c.AsLoadoutItem().Name, StringComparer.OrdinalIgnoreCase);
        var lines = entries.Where(e => !e.IsSeparator && !e.IsForeign).ToArray();

        var disabledByList = lines.Where(e => !e.Enabled).Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabledByList = lines.Where(e => e.Enabled).Select(e => e.Name).Where(n => !disabledByList.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool? EnabledFor(string name) => disabledByList.Contains(name) ? false : enabledByList.Contains(name) ? true : null;

        var winnerToLoser = new List<ModOrderEntry>(children.Count);
        var dropped = new List<ModOrderDrop>();
        var droppedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Game-root groups claim their names first so a line of the same name cannot lift one out of the bottom of the order.
        var roots = gameRootGroupNames.Order(StringComparer.OrdinalIgnoreCase).Where(n => byName.ContainsKey(n) && placed.Add(n)).ToArray();

        void Place(string name)
        {
            if (byName.TryGetValue(name, out var group) && placed.Add(name))
                winnerToLoser.Add(new ModOrderEntry(group.LoadoutItemGroupId, EnabledFor(name)));
        }

        Place(Mo2Layout.OverwriteGroup);

        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            listed.Add(line.Name);
            if (byName.ContainsKey(line.Name)) Place(line.Name);
            else if (line.Enabled && droppedNames.Add(line.Name))
                dropped.Add(new ModOrderDrop(line.Name, "no mod of that name was installed"));
        }

        var unlisted = mods
            .Select(c => c.AsLoadoutItem().Name)
            .Where(name => !placed.Contains(name) && !listed.Contains(name))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var name in unlisted)
            logger.LogWarning("Mod '{Mod}' of '{List}' has no line in modlist.txt, so it is placed below the listed mods", name, listName);
        foreach (var name in unlisted.Reverse()) Place(name);

        foreach (var settings in settingsGroups) winnerToLoser.Add(new ModOrderEntry(settings.LoadoutItemGroupId, null));
        foreach (var name in roots.Reverse()) winnerToLoser.Add(new ModOrderEntry(byName[name].LoadoutItemGroupId, EnabledFor(name)));

        return ModOrder.Create(winnerToLoser, dropped);
    }
}
