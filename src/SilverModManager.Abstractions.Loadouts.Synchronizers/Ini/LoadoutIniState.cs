using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;

/// <summary>
/// What a loadout remembers about one of the game's INI files. The baseline is kept off the file's real path (a
/// <c>LoadoutFile</c> there would be a second writer of the same path and show up as a conflict).
/// </summary>
public partial class LoadoutIniState : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutIniState";

    /// <summary>The loadout that owns the baseline.</summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>
    /// The INI's path. Item1 is the owning loadout, like <see cref="LoadoutItemWithTargetPath.TargetPath"/>.
    /// </summary>
    public static readonly GamePathParentAttribute Path = new(Namespace, nameof(Path));

    /// <summary>The INI as the loadout wants it before any tweak is merged in, as a hash into the file store.</summary>
    public static readonly HashAttribute Baseline = new(Namespace, nameof(Baseline));

    /// <summary>
    /// The bytes last written to the game folder for this INI, as a hash into the file store. They are stored
    /// so a later ingest can diff the live file against what was written. Absent until the first write.
    /// </summary>
    public static readonly HashAttribute LastBakedHash = new(Namespace, nameof(LastBakedHash)) { IsOptional = true };
}
