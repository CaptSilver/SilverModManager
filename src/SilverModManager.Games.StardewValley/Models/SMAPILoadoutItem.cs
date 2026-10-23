using JetBrains.Annotations;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.StardewValley.Models;

[PublicAPI]
[Include<LoadoutItemGroup>]
public partial class SMAPILoadoutItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.StardewValley.SMAPILoadoutItem";

    /// <summary>
    /// The version of SMAPI.
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version)) { IsOptional = true };

    /// <summary>
    /// Reference to the mod database loadout file.
    /// </summary>
    public static readonly ReferenceAttribute<SMAPIModDatabaseLoadoutFile> ModDatabase = new(Namespace, nameof(ModDatabase)) { IsOptional = true};
}
