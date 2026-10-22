using NexusMods.Games.FromSoft.Build;
using NexusMods.Games.FromSoft.Formats;
using NexusMods.Games.FromSoft.Heal;

namespace NexusMods.Games.FromSoft.Merge;

/// <summary>
/// The checks that surround folding the mods' regulations onto the installed game's. They apply only when the mods
/// were built for another build than the installed one: at the installed build the ancestor and the base are the same
/// file, so there is nothing to transplant and the fold is the plain merge.
/// </summary>
/// <param name="IsRebase">Whether the contributors' agreed build is not the one of the game's regulation they fold onto.</param>
/// <param name="Gate">The layout problems with moving the contributors' rows onto the game's regulation; empty when it is safe.</param>
/// <param name="Verify">The problems with the merged regulation; empty when it is a faithful rebase.</param>
public sealed record RegulationRebase(
    Func<byte[], IReadOnlyList<ProviderFile>, bool> IsRebase,
    Func<byte[], IReadOnlyList<(string ModId, byte[] Regulation)>, IReadOnlyList<string>> Gate,
    Func<byte[], byte[], IReadOnlyList<(string ModId, byte[] Regulation)>, IReadOnlyList<string>> Verify)
{
    /// <summary>
    /// The real checks. <paramref name="versionOf"/> is asked for the contributors' build stamp, which the caller
    /// remembers by hash. Whether to rebase is read from the game's own regulation, so a merge at the installed build
    /// never needs the executable or Steam's build id; <paramref name="live"/> is asked only by the verifier.
    /// </summary>
    public static RegulationRebase For(Func<ProviderFile, string> versionOf, Func<BuildIdentity> live, FromSoftFormatTraits traits)
        => new(
            (game, contributors) => !versionOf(contributors[0]).Equals(Regulation.Version(Regulation.Unpack(game, traits)), StringComparison.Ordinal),
            (baseline, mods) => LayoutGate.Check(baseline, mods, traits),
            (merged, baseline, mods) => RebaseVerifier.Verify(merged, baseline, mods, Installed(live()), traits));

    private static BuildIdentity Installed(BuildIdentity live)
        => live.RegulationVersion is null
            ? throw new MergeException("the installed game has no regulation version, so the mods' regulations cannot be rebased onto it")
            : live;
}
