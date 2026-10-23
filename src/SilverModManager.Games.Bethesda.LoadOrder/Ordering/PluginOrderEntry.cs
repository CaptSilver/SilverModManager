namespace SilverModManager.Games.Bethesda.LoadOrder.Ordering;

/// <summary>
/// One plugin as the resolver sees it.
/// </summary>
/// <param name="Name">File name, matched with <see cref="PluginNameComparer"/>.</param>
/// <param name="IsMaster">Whether the game treats the file as a master (flag or extension, per game).</param>
/// <param name="IsBlueprint">The blueprint flag. Only a blueprint that is also a master gets the blueprint rules; libloadorder's <c>is_blueprint_master</c> is the same conjunction.</param>
/// <param name="Masters">Masters from the plugin header; may name plugins that are not installed.</param>
/// <param name="SortIndex">The user's position, lower loads first; null for plugins the user has not placed.</param>
/// <param name="EarlyLoaderIndex">Position in the game's hardcoded early-loading list, null when not hardcoded.</param>
public sealed record PluginOrderEntry(
    string Name,
    bool IsMaster,
    bool IsBlueprint,
    IReadOnlyList<string> Masters,
    int? SortIndex,
    int? EarlyLoaderIndex)
{
    /// <summary>True for a master with the blueprint flag, the only kind the blueprint rules apply to.</summary>
    public bool IsBlueprintMaster => IsMaster && IsBlueprint;

    /// <summary>True when the game loads the file ahead of any list. A blueprint master is hardcoded as an early loader in some games but never loads early.</summary>
    public bool LoadsEarly => EarlyLoaderIndex is not null && !IsBlueprintMaster;
}
