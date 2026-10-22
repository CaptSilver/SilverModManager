namespace NexusMods.Abstractions.Games;

/// <summary>
/// Extra per-row information a sort item can offer the Load Order page. An <see cref="IReactiveSortItem"/>
/// that also implements this gets the columns the page shows for it; items that do not are shown by name only.
/// </summary>
public interface ISortItemDetails
{
    /// <summary>
    /// Short display text for the item's flags (for example "ESM, light"), or null when it has none.
    /// </summary>
    string? Flags { get; }

    /// <summary>
    /// Names of the items this one requires to be loaded before it.
    /// </summary>
    IReadOnlyList<string> Masters { get; }

    /// <summary>
    /// True when the item cannot be moved by the user.
    /// </summary>
    bool IsLocked { get; }

    /// <summary>
    /// True when the item takes part in the game's load order.
    /// </summary>
    bool IsEnabled { get; }
}
