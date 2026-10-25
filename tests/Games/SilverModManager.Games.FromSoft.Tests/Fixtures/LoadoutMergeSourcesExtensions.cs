using SilverModManager.Games.FromSoft.Merge;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Tests.Fixtures;

public static class LoadoutMergeSourcesExtensions
{
    /// <summary>The ancestor of a path whose contributors all branched from one build; fails if they did not.</summary>
    public static byte[]? SingleVanilla(
        this LoadoutMergeSources sources, Loadout.ReadOnly loadout, string relPath, IReadOnlyList<ProviderFile> contributors,
        MergeSourceMode mode, ICollection<IMergeNote>? notes = null)
        => sources.Ancestors(loadout, relPath, contributors, mode, notes)?.Groups.Single().Bytes;
}
