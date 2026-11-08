using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Wabbajack.Models;

/// <summary>
/// A mod group a modlist install created. It records the MO2 <c>mods/&lt;Folder&gt;</c> name the group was built from,
/// because the group's <see cref="LoadoutItem.Name"/> is what the user sees and may rename.
/// </summary>
[PublicAPI]
[Include<LoadoutItemGroup>]
public partial class WabbajackModGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Wabbajack.ModGroup";

    /// <summary>The folder name under <c>mods/</c> in the MO2 instance the list describes.</summary>
    public static readonly StringAttribute Folder = new(Namespace, nameof(Folder)) { IsIndexed = true };
}
