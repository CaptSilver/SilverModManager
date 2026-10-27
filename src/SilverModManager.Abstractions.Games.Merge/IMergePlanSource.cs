using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.Merge;

/// <summary>
/// Where a <see cref="MergedIntrinsicFile"/> gets its bytes: the merge plan the preflight already checked.
/// </summary>
public interface IMergePlanSource
{
    /// <summary>
    /// The merged bytes the next apply writes for <paramref name="relPath"/>, planned in Apply mode; null when the plan
    /// has no merge for it.
    /// </summary>
    byte[]? MergedBytes(Loadout.ReadOnly loadout, string relPath);
}
