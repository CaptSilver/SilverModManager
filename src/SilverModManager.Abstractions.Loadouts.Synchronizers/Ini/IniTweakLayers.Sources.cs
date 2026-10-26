using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using Hash = NexusMods.Hashing.xxHash3.Hash;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>A tweak file for an INI as the editor lists it, whether or not it is applied.</summary>
/// <param name="Name">The part of the file name before the bracketed target.</param>
/// <param name="SourceName">The mod the file belongs to, or the game settings group for the loadout's own tweak.</param>
/// <param name="Hash">The tweak's bytes in the file store.</param>
/// <param name="FileId">The loadout item holding the file.</param>
/// <param name="IsLoadoutTweak">Whether this is the loadout's own tweak.</param>
/// <param name="IsSwitchedOn">Whether the file itself is on, apart from the mod above it.</param>
/// <param name="IsEnabled">Whether the bake merges it: the file and every group above it are on.</param>
public readonly record struct IniTweakSource(
    string Name,
    string SourceName,
    Hash Hash,
    EntityId FileId,
    bool IsLoadoutTweak,
    bool IsSwitchedOn,
    bool IsEnabled);

public static partial class IniTweakLayers
{
    /// <summary>
    /// Every tweak for <paramref name="target"/>, disabled ones included, in the order the bake merges them. The
    /// enabled ones are exactly <see cref="For"/>.
    /// </summary>
    public static IReadOnlyList<IniTweakSource> All(Loadout.ReadOnly loadout, GamePath target)
    {
        var priorities = new Dictionary<EntityId, ConflictPriority>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
            priorities[row.TargetId] = row.Priority;

        var found = new List<(ConflictPriority Priority, bool IsLoadoutTweak, string Path, IniTweakSource Source)>();
        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var item = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
            var path = ((GamePath)file.AsLoadoutItemWithTargetPath().TargetPath).Path;
            if (!TryParse(path, target.FileName.ToString(), out var name)) continue;

            var isLoadoutTweak = name.Equals(LoadoutTweakName, StringComparison.OrdinalIgnoreCase) && InGameSettingsGroup(item);
            var source = new IniTweakSource(name, SourceNameOf(item, priorities), file.Hash, file.Id, isLoadoutTweak, !item.IsDisabled, item.IsEnabled());
            found.Add((PriorityOf(item, priorities), isLoadoutTweak, path.ToString(), source));
        }

        return found
            .OrderBy(f => f.IsLoadoutTweak)
            .ThenBy(f => f.Priority.Value)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f.Source)
            .ToList();
    }

    // The name of the group the priority belongs to (the mod), else of the file's own parent.
    private static string SourceNameOf(LoadoutItem.ReadOnly item, Dictionary<EntityId, ConflictPriority> priorities)
    {
        var above = item.GetThisAndParents().Skip(1).ToList();
        var owner = above.FirstOrDefault(a => priorities.ContainsKey(a.Id));
        if (owner.IsValid()) return owner.Name;
        return above.Count > 0 ? above[0].Name : item.Name;
    }
}
