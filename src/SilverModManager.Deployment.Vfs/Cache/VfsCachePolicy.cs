namespace SilverModManager.Deployment.Vfs;

/// <summary>
/// How long the kernel may trust what the tree tells it. The tree is the only writer of the
/// mounted folder, so long lifetimes are safe and spare Wine's constant case-variant probing a trip
/// to userspace each time; <see cref="Disabled"/> exists to rule the cache out when chasing a bug.
/// </summary>
/// <param name="NegativeTtl">How long a missing name stays missing.</param>
/// <param name="KeepCache">Whether pages cached for a file survive it being opened again.</param>
public sealed record VfsCachePolicy(TimeSpan EntryTtl, TimeSpan AttrTtl, TimeSpan NegativeTtl, bool KeepCache)
{
    public static VfsCachePolicy Default { get; } = new(TimeSpan.FromHours(24), TimeSpan.FromHours(24), TimeSpan.FromHours(1), KeepCache: true);

    public static VfsCachePolicy Disabled { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, KeepCache: false);
}
