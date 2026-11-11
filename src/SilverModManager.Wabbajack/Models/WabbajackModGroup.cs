using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Hashes;

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

    /// <summary>
    /// The xxHash64 of the archive most of the folder's files were taken from. Absent for a folder the list generates.
    /// The group's library link follows it: the link is the top-level library file holding this archive.
    /// </summary>
    public static readonly FileIdentityAttribute MainArchive = new(Namespace, nameof(MainArchive)) { IsOptional = true };
}
