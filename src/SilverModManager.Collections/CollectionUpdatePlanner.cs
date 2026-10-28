using SilverModManager.Abstractions.NexusModsLibrary.Models;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Collections;

/// <summary>A mod installed from the old revision, as the planner sees it.</summary>
/// <param name="Group">The mod's group in the loadout.</param>
/// <param name="Name">Name to show the user.</param>
/// <param name="File">Identifies the exact file the mod was installed from.</param>
/// <param name="Mod">Identifies the mod regardless of which file of it is installed.</param>
/// <param name="Spec">Everything else the collection says about how to install the mod (choices, patches, file list). Null when unknown, which counts as equal to any.</param>
/// <param name="IsRequired">Whether the old revision listed the mod as required.</param>
public sealed record InstalledMod(LoadoutItemGroupId Group, string Name, string File, string Mod, string? Spec, bool IsRequired);

/// <summary>A mod the new revision lists, as the planner sees it.</summary>
/// <inheritdoc cref="InstalledMod"/>
public sealed record AvailableMod(CollectionDownloadId Download, string Name, string File, string Mod, string Spec, bool IsRequired);

/// <summary>An installed mod that the new revision lists unchanged.</summary>
public sealed record KeptMod(InstalledMod Installed, AvailableMod Available);

/// <summary>An installed mod that the new revision lists as a different file or with different install settings.</summary>
public sealed record ChangedMod(InstalledMod Installed, AvailableMod Available);

/// <summary>What updating an installed collection to a new revision does to each mod.</summary>
public sealed record CollectionUpdatePlan(
    IReadOnlyList<KeptMod> Kept,
    IReadOnlyList<ChangedMod> Changed,
    IReadOnlyList<AvailableMod> Added,
    IReadOnlyList<InstalledMod> Dropped)
{
    /// <summary>
    /// The mods the install has to run for: everything required that isn't installed yet, and the new files of changed mods.
    /// A new optional mod is left out, so an optional mod the user never installed stays that way.
    /// </summary>
    public IEnumerable<AvailableMod> ToInstall => Changed.Select(static changed => changed.Available).Concat(Added.Where(static mod => mod.IsRequired));
}

/// <summary>
/// Decides, for an installed collection and the revision it is updated to, which mods stay as they are, which are
/// replaced, which are new and which are gone. Pure: the caller describes the mods, so the rules are testable without a database.
/// </summary>
public static class CollectionUpdatePlanner
{
    /// <summary>Matches the installed mods against the new revision's mods.</summary>
    public static CollectionUpdatePlan Plan(IReadOnlyList<InstalledMod> installed, IReadOnlyList<AvailableMod> available)
    {
        var oldLeft = installed.ToList();
        var newLeft = available.ToList();
        var kept = new List<KeptMod>();
        var changed = new List<ChangedMod>();

        // Same file and same install settings: nothing to redo. This goes first so that when a mod has several
        // files, the one that didn't change isn't paired with the one that did.
        foreach (var old in installed)
        {
            var match = newLeft.FirstOrDefault(next => next.File == old.File && (old.Spec is null || old.Spec == next.Spec));
            if (match is null) continue;
            kept.Add(new KeptMod(old, match));
            oldLeft.Remove(old);
            newLeft.Remove(match);
        }

        // Same mod, different file or settings: the new install takes the old one's place.
        foreach (var old in oldLeft.ToArray())
        {
            var match = newLeft.FirstOrDefault(next => next.Mod == old.Mod);
            if (match is null) continue;
            changed.Add(new ChangedMod(old, match));
            oldLeft.Remove(old);
            newLeft.Remove(match);
        }

        return new CollectionUpdatePlan(kept, changed, newLeft, oldLeft);
    }
}
