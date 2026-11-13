using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.NexusModsLibrary;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// The pinned winners of one merged path, resolved to the names the fold compares owners by. A fold reads
/// <see cref="Winner"/> at every contested item and says through <see cref="Took"/> when it let the pinned mod's
/// version replace what it had, which is how the planner learns whether a pin changed anything.
/// </summary>
public sealed class PathChoices
{
    private readonly Dictionary<string, string> _winners;
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

    public PathChoices(IEnumerable<(string Key, string Winner)> winners, IEnumerable<(string Key, string Reason)>? stale = null)
    {
        _winners = winners.ToDictionary(w => w.Key, w => w.Winner, StringComparer.Ordinal);
        Stale = [.. stale ?? []];
        All = [.. _winners.Select(w => (w.Key, w.Value))];
    }

    /// <summary>The contributor the pin on <paramref name="key"/> resolves to, or null when there is no pin.</summary>
    public string? Winner(string key) => _winners.GetValueOrDefault(key);

    /// <summary>Records that the pinned mod's version took the item at its step of the fold.</summary>
    public void Took(string key, string modName) => _owners[key] = modName;

    /// <summary>What <see cref="Took"/> recorded for <paramref name="key"/>.</summary>
    public string? Owner(string key) => _owners.GetValueOrDefault(key);

    /// <summary>Every pin that resolved to a contributor of the path.</summary>
    public IReadOnlyList<(string Key, string Winner)> All { get; }

    /// <summary>Pins that no contributor of the path satisfies, with the reason each is ignored.</summary>
    public IReadOnlyList<(string Key, string Reason)> Stale { get; }

    /// <summary>A copy that has recorded nothing, so a second plan of the same pins starts from the pins alone.</summary>
    public PathChoices Fresh() => new(All, Stale);
}

/// <summary>Reads a loadout's <see cref="LoadoutMergeChoice"/> rows and resolves them against a merge index.</summary>
public static class MergeChoices
{
    /// <summary>
    /// The pins of each path that has any, keyed by the path as the pin spells it (any casing). A pin resolves to the
    /// contributor whose Nexus mod id equals the pin's, else to the contributor whose group is the pinned one; a pin
    /// neither finds is stale.
    /// </summary>
    public static IReadOnlyDictionary<string, PathChoices> For(IDb db, LoadoutId loadout, MergePathIndex index)
    {
        var resolved = new Dictionary<string, PathChoices>(StringComparer.OrdinalIgnoreCase);
        foreach (var byPath in LoadoutMergeChoice.FindByLoadout(db, loadout).GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase))
        {
            var contributors = index.Lookup(byPath.Key)?.Contributors ?? [];
            var winners = new List<(string, string)>();
            var stale = new List<(string, string)>();
            foreach (var choice in byPath.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                if (Resolve(db, choice, contributors) is { } winner) winners.Add((choice.Key, winner));
                else stale.Add((choice.Key, $"{NameOf(db, choice.WinnerGroupId)} no longer provides {byPath.Key}"));
            }
            resolved[byPath.Key] = new PathChoices(winners, stale);
        }
        return resolved;
    }

    /// <summary>The Nexus mod a group was installed from, or null for a local mod.</summary>
    public static ModId? NexusModIdOf(IDb db, LoadoutItemGroupId group)
    {
        var item = LoadoutItemGroup.Load(db, group);
        if (!item.TryGetAsLibraryLinkedLoadoutItem(out var linked)) return null;
        return linked.LibraryItem.TryGetAsNexusModsLibraryItem(out var nexus) ? nexus.ModPageMetadata.Uid.ModId : null;
    }

    private static string? Resolve(IDb db, LoadoutMergeChoice.ReadOnly choice, IReadOnlyList<ProviderFile> contributors)
    {
        if (LoadoutMergeChoice.WinnerModId.TryGetValue(choice, out var pinnedMod))
        {
            var byMod = contributors.FirstOrDefault(c => c.ModId is { } id && NexusModIdOf(db, LoadoutItemGroupId.From(id.Value)) is { } mod && mod.Value == pinnedMod);
            if (byMod.ModName is not null) return byMod.ModName;
        }
        var byGroup = contributors.FirstOrDefault(c => c.ModId is { } id && id.Value == choice.WinnerGroupId.Value);
        return byGroup.ModName;
    }

    private static string NameOf(IDb db, LoadoutItemGroupId group)
    {
        var item = LoadoutItem.Load(db, LoadoutItemId.From(group.Value));
        return item.IsValid() ? item.Name : $"group {group.Value}";
    }
}
