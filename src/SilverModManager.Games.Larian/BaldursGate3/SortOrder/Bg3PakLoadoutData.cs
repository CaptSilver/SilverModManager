using DynamicData.Kernel;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Loadouts;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions;

namespace SilverModManager.Games.Larian.BaldursGate3.SortOrder;

/// <summary>
/// A pak the loadout offers to the game. <see cref="IsEnabled"/> is the owning mod's state, or the state of the
/// group of game-installed content for a pak the game put there; the pak's own on/off switch lives on the sort
/// order row.
/// </summary>
/// <param name="ModGroupId">The mod that supplies the pak, or the game-installed item for a pak the game installed.</param>
public sealed record Bg3PakLoadoutData(
    string Name,
    Hash Hash,
    bool IsEnabled,
    string ModName,
    EntityId? ModGroupId,
    bool IsGameInstalled) : ISortItemLoadoutData<SortItemKey<string>>
{
    /// <summary>The name lower-cased, the way every pak lookup compares it.</summary>
    public SortItemKey<string> Key { get; } = Bg3PakSortData.KeyFor(Name);

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
