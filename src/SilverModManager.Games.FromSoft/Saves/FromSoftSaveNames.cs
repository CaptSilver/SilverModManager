using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Games.FromSoft.Saves;

/// <summary>The save files of a game, by file name inside the account's save folder.</summary>
/// <param name="Vanilla">The save file the game writes.</param>
/// <param name="Modded">
/// The save a modded session is pointed at so it never opens <paramref name="Vanilla"/>; <c>null</c> when the game's
/// loader cannot redirect saves, in which case no profile writes a savefile line and the vanilla guard copies only
/// <paramref name="Vanilla"/>. It is the name used when a loadout does not keep its saves of its own; one that does
/// uses <see cref="ModdedFor"/>.
/// </param>
/// <param name="SeamlessCoop">The save Seamless Co-op writes; <c>null</c> when the game has no Seamless Co-op.</param>
/// <param name="LegacyModded">
/// The save a modded session of another loader (the one this app replaces) wrote; <c>null</c> when there is none.
/// A loadout adopts it when no <paramref name="Modded"/> save exists.
/// </param>
public sealed record FromSoftSaveNames(string Vanilla, string? Modded, string? SeamlessCoop, string? LegacyModded = null)
{
    /// <summary>
    /// The copies that bring the save a modded session played on into <paramref name="target"/>, or null when there
    /// is nothing to bring or the slot already has a save. The modded save is the source, the other loader's save
    /// only when there is no modded one; a backup alone is not worth bringing over.
    /// </summary>
    public SaveAdoptionPlan? PlanAdoption(string target, AbsolutePath saveRoot)
    {
        var destination = saveRoot / target;
        if (destination.FileExists) return null;

        foreach (var name in new[] { Modded, LegacyModded }.OfType<string>())
        {
            var source = saveRoot / name;
            if (!source.FileExists) continue;

            var copies = new List<SaveAdoptionPlan.Copy> { new(source, destination) };
            var sourceBackup = saveRoot / (name + ".bak");
            var destinationBackup = saveRoot / (target + ".bak");
            if (sourceBackup.FileExists && !destinationBackup.FileExists) copies.Add(new SaveAdoptionPlan.Copy(sourceBackup, destinationBackup));

            return new SaveAdoptionPlan($"Copy {name} to {target}, so this loadout continues from the save you play today.", copies);
        }

        return null;
    }

    /// <summary>
    /// The modded save of one loadout: <see cref="Modded"/> with its last <c>_</c>-separated word, the slot name,
    /// replaced by the loadout's slot id (<c>ER0000_silver.sl2</c> becomes <c>ER0000_&lt;id&gt;.sl2</c>). <c>null</c>
    /// when the game has no modded save.
    /// </summary>
    public string? ModdedFor(Loadout.ReadOnly loadout)
        => SlotParts() is var (prefix, extension) ? $"{prefix}{LoadoutSaveSlot.IdOf(loadout)}{extension}" : null;

    /// <summary>
    /// True when <paramref name="fileName"/> is a modded save named for some loadout: the shape <see cref="ModdedFor"/>
    /// produces, with an id this app would generate. Anything else with the same prefix was written by someone else.
    /// </summary>
    public bool IsLoadoutSlotName(string fileName)
    {
        if (SlotParts() is not var (prefix, extension)) return false;
        if (fileName.Length <= prefix.Length + extension.Length) return false;
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal) || !fileName.EndsWith(extension, StringComparison.Ordinal)) return false;

        return LoadoutSaveSlot.IsSlotId(fileName.Substring(prefix.Length, fileName.Length - prefix.Length - extension.Length));
    }

    /// <summary>
    /// The slot id in a loadout's save name (the <c>&lt;id&gt;</c> of <c>ER0000_&lt;id&gt;.sl2</c>), or <c>null</c>
    /// when <paramref name="fileName"/> is not such a name.
    /// </summary>
    public string? LoadoutSlotOf(string fileName)
    {
        if (!IsLoadoutSlotName(fileName) || SlotParts() is not var (prefix, extension)) return null;

        return fileName.Substring(prefix.Length, fileName.Length - prefix.Length - extension.Length);
    }

    private (string Prefix, string Extension)? SlotParts()
    {
        if (Modded is null) return null;

        var underscore = Modded.LastIndexOf('_');
        var dot = Modded.LastIndexOf('.');
        // A modded name with no slot word or no extension has no per-loadout form to derive.
        if (underscore < 0 || dot < underscore) return null;
        return (Modded[..(underscore + 1)], Modded[dot..]);
    }
}
