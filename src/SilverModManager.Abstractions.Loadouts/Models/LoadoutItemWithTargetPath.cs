using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// Represents a loadout item with a target path.
/// </summary>
[Include<LoadoutItem>]
[PublicAPI]
public partial class LoadoutItemWithTargetPath : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.LoadoutItemWithTargetPath";

    /// <summary>
    /// The target path.
    /// </summary>
    public static readonly GamePathParentAttribute TargetPath = new(Namespace, nameof(TargetPath)) { IsIndexed = true };
}
