using NexusMods.Paths;

namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// Refuses to promote more than a cap's worth of staged bytes. It has its own limit and does not go
/// through the copy backend's backup cap: tool output written into the game folder in one session
/// can run to many gigabytes.
/// </summary>
public static class PromotionSizePolicy
{
    /// <summary>How many files an over-cap verdict names.</summary>
    public const int LargestCount = 10;

    public static SizeVerdict Evaluate(StagingReport report, Size cap)
    {
        if (report.Total.Value <= cap.Value) return SizeVerdict.Ok;
        return new SizeVerdict.OverCap(report.Total, cap, report.Files.Take(LargestCount).ToArray());
    }
}

/// <summary>The outcome of <see cref="PromotionSizePolicy.Evaluate"/>.</summary>
public abstract record SizeVerdict
{
    private SizeVerdict() { }

    public static SizeVerdict Ok { get; } = new WithinCap();

    public sealed record WithinCap : SizeVerdict;

    /// <param name="Total">All staged bytes.</param>
    /// <param name="Cap">The limit that was exceeded.</param>
    /// <param name="Largest">The biggest staged files, largest first, so a person can pick what to discard.</param>
    public sealed record OverCap(Size Total, Size Cap, IReadOnlyList<StagedFile> Largest) : SizeVerdict;
}
