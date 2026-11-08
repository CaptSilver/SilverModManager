using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>A tweak file for an INI as the editor lists it, whether or not it is applied.</summary>
/// <param name="Name">The part of the file name before the bracketed target.</param>
/// <param name="SourceName">The collection or modlist the file belongs to, else the mod, or the game settings group for the loadout's own tweak.</param>
/// <param name="Identity">The tweak's bytes in the file store.</param>
/// <param name="FileId">The loadout item holding the file.</param>
/// <param name="IsLoadoutTweak">Whether this is the loadout's own tweak.</param>
/// <param name="IsSwitchedOn">Whether the file itself is on, apart from the mod above it.</param>
/// <param name="IsEnabled">Whether the bake merges it: the file and every group above it are on.</param>
/// <param name="Kind">Who supplies the file: the user, a collection or modlist, or a single mod.</param>
public readonly record struct IniTweakSource(
    string Name,
    string SourceName,
    FileIdentity Identity,
    EntityId FileId,
    bool IsLoadoutTweak,
    bool IsSwitchedOn,
    bool IsEnabled,
    IniTweakKind Kind);

/// <summary>Who supplies a tweak file.</summary>
public enum IniTweakKind
{
    /// <summary>The loadout's own tweak, the one the INI editor writes.</summary>
    User,

    /// <summary>A file inside a collection or modlist group.</summary>
    List,

    /// <summary>A file of a mod outside any collection.</summary>
    Mod,
}

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

            var isLoadoutTweak = name.Equals(LoadoutTweakName, StringComparison.OrdinalIgnoreCase) && IsUsersTweakFile(item);
            var source = new IniTweakSource(name, SourceNameOf(item, priorities), file.Identity, file.Id, isLoadoutTweak, !item.IsDisabled, item.IsEnabled(), KindOf(item, isLoadoutTweak));
            found.Add((PriorityOf(item, priorities), isLoadoutTweak, path.ToString(), source));
        }

        return found
            .OrderBy(f => f.IsLoadoutTweak)
            .ThenBy(f => f.Priority.Value)
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .Select(f => f.Source)
            .ToList();
    }

    /// <summary>
    /// The user's own tweak of <paramref name="target"/>: the one that merges last, so the one that wins when a past
    /// bug left two. Null when the loadout has none.
    /// </summary>
    public static IniTweakSource? UserTweak(Loadout.ReadOnly loadout, GamePath target) =>
        All(loadout, target).Where(t => t.IsLoadoutTweak).Select(t => (IniTweakSource?)t).LastOrDefault();

    /// <summary>
    /// The enabled tweaks that name an INI other than <paramref name="knownTargets"/>: fragments for a file the game
    /// does not read, which no bake picks up. Each comes with the target file name it asked for.
    /// </summary>
    public static IReadOnlyList<(string Target, IniTweakSource Source)> EnabledForOtherTargets(Loadout.ReadOnly loadout, IReadOnlyCollection<string> knownTargets)
    {
        var priorities = new Dictionary<EntityId, ConflictPriority>();
        foreach (var row in LoadoutItemGroupPriority.FindByLoadout(loadout.Db, loadout))
            priorities[row.TargetId] = row.Priority;

        var found = new List<(string Path, string Target, IniTweakSource Source)>();
        foreach (var file in LoadoutItem.FindByLoadout(loadout.Db, loadout).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile())
        {
            var item = file.AsLoadoutItemWithTargetPath().AsLoadoutItem();
            var path = ((GamePath)file.AsLoadoutItemWithTargetPath().TargetPath).Path;
            if (!item.IsEnabled() || !TryParse(path, out var name, out var target)) continue;
            if (knownTargets.Contains(target, StringComparer.OrdinalIgnoreCase)) continue;

            var isLoadoutTweak = name.Equals(LoadoutTweakName, StringComparison.OrdinalIgnoreCase) && IsUsersTweakFile(item);
            found.Add((path.ToString(), target, new IniTweakSource(name, SourceNameOf(item, priorities), file.Identity, file.Id, isLoadoutTweak, true, true, KindOf(item, isLoadoutTweak))));
        }

        return found.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => (f.Target, f.Source)).ToList();
    }

    private static IniTweakKind KindOf(LoadoutItem.ReadOnly item, bool isLoadoutTweak)
    {
        if (isLoadoutTweak) return IniTweakKind.User;
        return CollectionOf(item).IsValid() ? IniTweakKind.List : IniTweakKind.Mod;
    }

    // The nearest collection or modlist group above the file, or an invalid item when it sits in none.
    private static LoadoutItem.ReadOnly CollectionOf(LoadoutItem.ReadOnly item) =>
        item.GetThisAndParents().Skip(1).FirstOrDefault(a => a.TryGetAsLoadoutItemGroup(out var group) && group.TryGetAsCollectionGroup(out _));

    // The collection's name when the group that owns the priority row is the collection itself or a game settings
    // group (a modlist's own tweak, which has no mod name of its own). A mod inside a collection keeps its own name, so
    // a card or a listing can say which mod ships a fragment. Otherwise the name of the group the priority belongs to,
    // else of the file's own parent.
    private static string SourceNameOf(LoadoutItem.ReadOnly item, Dictionary<EntityId, ConflictPriority> priorities)
    {
        var above = item.GetThisAndParents().Skip(1).ToList();
        var owner = above.FirstOrDefault(a => priorities.ContainsKey(a.Id));
        var collection = CollectionOf(item);
        if (collection.IsValid() && owner.IsValid() && (owner.Id == collection.Id || IsSettingsGroup(owner))) return collection.Name;

        if (owner.IsValid()) return owner.Name;
        return above.Count > 0 ? above[0].Name : item.Name;
    }

    private static bool IsSettingsGroup(LoadoutItem.ReadOnly item) =>
        item.TryGetAsLoadoutItemGroup(out var group) && group.IsGameSettingsGroup;
}
