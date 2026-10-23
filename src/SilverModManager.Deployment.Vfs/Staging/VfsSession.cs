using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// One mounted run of the tree: the tree itself, where it stages the game's writes, and what to do
/// with those writes when the run ends.
/// </summary>
public sealed class VfsSession
{
    private readonly StagingPromoter _promoter;
    private readonly LoadoutId _target;

    public VfsSession(VfsTree tree, StagingDirectory staging, StagingPromoter promoter, LoadoutId target)
    {
        Tree = tree;
        Staging = staging;
        _promoter = promoter;
        _target = target;
    }

    public VfsTree Tree { get; }

    public StagingDirectory Staging { get; }

    /// <summary>
    /// Throws away what the game wrote when the session ends instead of promoting it, for tools whose
    /// output is not worth keeping.
    /// </summary>
    public bool DiscardStagingOnUnmount { get; set; }

    /// <summary>
    /// Ends the session's staging: discards it when <see cref="DiscardStagingOnUnmount"/> is set,
    /// otherwise promotes it into the target loadout.
    /// </summary>
    public async Task<PromotionResult> FinishSession(CancellationToken token = default)
    {
        if (!DiscardStagingOnUnmount) return await _promoter.Promote(_target, token);

        _promoter.DiscardAll();
        return PromotionResult.NothingToDo;
    }
}
