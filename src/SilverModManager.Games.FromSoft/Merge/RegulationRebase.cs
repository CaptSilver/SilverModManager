using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Heal;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// The checks that surround folding the mods' regulations onto the installed game's. They apply only when the mods
/// were built for another build than the installed one: at the installed build the ancestor and the base are the same
/// file, so there is nothing to transplant and the fold is the plain merge. One mod on another build is enough, since
/// its rows are then compared against a different ancestor from the other mods'.
/// </summary>
/// <param name="IsRebase">Whether any contributor was built for another build than the game's regulation they fold onto.</param>
/// <param name="Gate">The layout problems with moving the contributors' rows onto the game's regulation; empty when it is safe.</param>
/// <param name="Verify">The problems with the merged regulation; empty when it is a faithful rebase. Takes each mod's own ancestor and the clashes the merge already reported, which are warnings and not failures.</param>
public sealed record RegulationRebase(
    Func<byte[], IReadOnlyList<ProviderFile>, bool> IsRebase,
    Func<byte[], IReadOnlyList<(string ModId, byte[] Regulation)>, IReadOnlyList<string>> Gate,
    Func<byte[], byte[], IReadOnlyList<(string ModId, byte[] Regulation, byte[] Ancestor)>, IReadOnlyList<RowConflict>, IReadOnlyList<string>> Verify)
{
    /// <summary>
    /// The real checks. <paramref name="versionOf"/> is asked for the contributors' build stamp, which the caller
    /// remembers by hash. Whether to rebase is read from the game's own regulation, so a merge at the installed build
    /// never needs the executable or Steam's build id; <paramref name="live"/> is asked only by the verifier.
    /// </summary>
    public static RegulationRebase For(Func<ProviderFile, string> versionOf, Func<BuildIdentity> live, FromSoftFormatTraits traits)
        => new(
            (game, contributors) =>
            {
                var gameVersion = Regulation.Version(Regulation.Unpack(game, traits));
                return contributors.Any(c => !versionOf(c).Equals(gameVersion, StringComparison.Ordinal));
            },
            (baseline, mods) => LayoutGate.Check(baseline, mods, traits),
            (merged, baseline, mods, conflicts) => RebaseVerifier.Verify(merged, baseline, mods, Installed(live()), traits, conflicts));

    private static BuildIdentity Installed(BuildIdentity live)
        => live.RegulationVersion is null
            ? throw new MergeException("the installed game has no regulation version, so the mods' regulations cannot be rebased onto it")
            : live;
}
