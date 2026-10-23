using NexusMods.Paths;

namespace NexusMods.Deployment.Vfs;

public enum PromotionStatus
{
    /// <summary>Nothing was staged and no promotion was waiting.</summary>
    NothingToDo,

    /// <summary>The staged changes are in the store and the loadout, and staging is empty.</summary>
    Promoted,

    /// <summary>An interrupted promotion was finished.</summary>
    Recovered,

    /// <summary>A staged file no longer matched its journaled hash; staging was moved aside and nothing was stored or recorded.</summary>
    Blocked,

    /// <summary>The staged files add up to more than the size cap; nothing was hashed, stored or removed.</summary>
    OverCap,
}

/// <param name="Status">What happened.</param>
/// <param name="Files">The staged files the promotion covered.</param>
/// <param name="RecoveryPath">For <see cref="PromotionStatus.Blocked"/>, the folder the staged files were moved to.</param>
/// <param name="Verdict">For <see cref="PromotionStatus.OverCap"/>, the totals and the largest staged files.</param>
public sealed record PromotionResult(PromotionStatus Status, IReadOnlyList<RelativePath> Files, AbsolutePath? RecoveryPath = null, SizeVerdict.OverCap? Verdict = null)
{
    public static PromotionResult NothingToDo { get; } = new(PromotionStatus.NothingToDo, []);
}
