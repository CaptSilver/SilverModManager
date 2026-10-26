using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;

/// <summary>
/// Pins which group provides one path of a loadout, whatever the group priorities say.
/// </summary>
public partial class LoadoutFileWinner : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutFileWinner";

    /// <summary>The loadout that owns the pin.</summary>
    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>
    /// The pinned path. Item1 is the owning loadout, like <see cref="LoadoutItemWithTargetPath.TargetPath"/>, so
    /// a join against the loadout items matches on the location and relative path only.
    /// </summary>
    public static readonly GamePathParentAttribute TargetPath = new(Namespace, nameof(TargetPath));

    /// <summary>The group whose file is deployed for <see cref="TargetPath"/>.</summary>
    public static readonly ReferenceAttribute<LoadoutItemGroup> WinnerGroup = new(Namespace, nameof(WinnerGroup));
}
