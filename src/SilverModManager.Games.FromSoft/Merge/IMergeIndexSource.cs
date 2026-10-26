using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// What a page that explains a loadout's package files needs from the FromSoft games: whether the loadout's game
/// keeps its mods in me3 packages, and the index of the paths its packages share.
/// </summary>
public interface IMergeIndexSource
{
    /// <summary>Whether the loadout's game loads its mods as me3 packages, in the order of the mods' priorities.</summary>
    bool ProvidesIndex(Loadout.ReadOnly loadout);

    /// <summary>
    /// The paths more than one package provides. Whether a path is merged depends only on who provides it, so the
    /// vanilla copies the real index also looks up in the game's archives are left out: a merge's strategy and
    /// outcome don't matter to a reader of the shared files.
    /// </summary>
    MergePathIndex? IndexOf(Loadout.ReadOnly loadout);

    /// <summary>
    /// The paths whose merge failed in the loadout's latest plan, in any casing. Those deploy whole, so a reader of the
    /// shared files still counts them. Empty until a plan has been made.
    /// </summary>
    IReadOnlySet<string> UnmergedPaths(Loadout.ReadOnly loadout);
}

/// <inheritdoc />
public sealed class LoadoutMergeIndexSource(LoadoutMergeSources sources, MergePlanCache plans) : IMergeIndexSource
{
    /// <inheritdoc />
    public bool ProvidesIndex(Loadout.ReadOnly loadout) => loadout.InstallationInstance.Game is IFromSoftGame;

    /// <inheritdoc />
    public MergePathIndex? IndexOf(Loadout.ReadOnly loadout)
        => loadout.InstallationInstance.Game is IFromSoftGame game
            ? MergePathIndex.Build(sources.Providers(loadout), game.Formats, new HashSet<string>())
            : null;

    /// <inheritdoc />
    public IReadOnlySet<string> UnmergedPaths(Loadout.ReadOnly loadout) => plans.UnmergedPaths(loadout.LoadoutId);
}
