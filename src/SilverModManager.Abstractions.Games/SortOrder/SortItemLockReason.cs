namespace SilverModManager.Abstractions.Games;

/// <summary>
/// Why a sort item cannot be switched on or off by the user.
/// </summary>
public enum SortItemLockReason
{
    /// <summary>The game always loads the item, so it can neither be moved nor switched off.</summary>
    AlwaysLoaded,

    /// <summary>
    /// The game installed the item. It follows the "Installed by the game" group's switch; the item itself can
    /// still be moved.
    /// </summary>
    GameInstalledGroup,
}
