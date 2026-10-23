using DynamicData.Kernel;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;

namespace NexusMods.Games.CreationEngine.SortOrder;

/// <summary>
/// A plugin the loadout offers to the game, with what its header says. <see cref="IsEnabled"/> is the owning
/// mod's state (the game-installed group's, for a plugin the game installed); the plugin's own on/off switch lives on the sort order row.
/// </summary>
/// <param name="Masters">Names from the plugin's header. Empty when the header could not be read.</param>
/// <param name="ModGroupId">The mod that supplies the plugin, null for a file of the game itself.</param>
public sealed record PluginLoadoutData(
    string Name,
    Hash Hash,
    bool IsEnabled,
    string ModName,
    EntityId? ModGroupId,
    bool IsMaster,
    IReadOnlyList<string> Masters) : ISortItemLoadoutData<SortItemKey<string>>
{
    /// <summary>Short text for the header flags ("ESM, light"), or null when it has none.</summary>
    public string? Flags { get; init; }

    /// <summary>Where the game loads the plugin among the early loaders, or null when it is not one of them.</summary>
    public int? EarlyLoaderIndex { get; init; }

    /// <summary>
    /// True when the game installed the plugin itself (a Creation). <see cref="IsEnabled"/> is then the switch of the
    /// "Installed by the game" group, and the plugin's own switch in the sort order is ignored.
    /// </summary>
    public bool IsGameInstalled { get; init; }

    /// <summary>The header's blueprint flag; the order only treats a blueprint that is also a master specially.</summary>
    public bool IsBlueprint { get; init; }

    /// <summary>The name lower-cased, the way every plugin lookup compares it.</summary>
    public SortItemKey<string> Key { get; } = PluginSortData.KeyFor(Name);

    // Declared by hand because the interface wants setters and a positional record only generates init.
    public bool IsEnabled { get; set; } = IsEnabled;
    public string ModName { get; set; } = ModName;
    public EntityId? ModGroupId { get; set; } = ModGroupId;

    Optional<LoadoutItemGroupId> ISortItemLoadoutData.ModGroupId
    {
        get => ModGroupId is { } id ? LoadoutItemGroupId.From(id) : Optional<LoadoutItemGroupId>.None;
        set => ModGroupId = value.HasValue ? value.Value.Value : null;
    }
}
