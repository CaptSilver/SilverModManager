using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// One piece of content the game installed, such as a Creation or an in-game mod. Its parent is the
/// <see cref="GameInstalledGroup"/> and its children are <see cref="GameInstalledFile"/>s.
/// </summary>
/// <remarks>
/// <see cref="Source"/> and <see cref="SourceId"/> together identify the item across reconciles.
/// </remarks>
[Include<LoadoutItemGroup>]
public partial class GameInstalledItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.GameInstalledItem";

    /// <summary>
    /// Which recognizer found the item: "SkyrimCreation", "StarfieldCreation", "Fallout4Creation" or
    /// "Bg3InGameMod". A string so a game can add a value without a schema change. It is persisted and part of
    /// the item's key, so a value is never renamed.
    /// </summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source));

    /// <summary>
    /// The item's id within its source: the catalog key or manifest id for Creations, the pak file name for BG3.
    /// </summary>
    public static readonly StringAttribute SourceId = new(Namespace, nameof(SourceId));

    /// <summary>
    /// The content's version, only when the game's record gives one that parses as digits and dots.
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version)) { IsOptional = true };

    /// <summary>
    /// "GameRecord" when the game's own record names the item, "FileNamePattern" when it was matched by file name.
    /// </summary>
    public static readonly StringAttribute Confidence = new(Namespace, nameof(Confidence));

    /// <summary>
    /// Whether "Treat as my file" is offered for the item.
    /// </summary>
    public static readonly BooleanAttribute CanBeClaimed = new(Namespace, nameof(CanBeClaimed));

    /// <summary>
    /// True when every file of the item is a file of the game's depot manifest.
    /// </summary>
    public static readonly BooleanAttribute DepotShipped = new(Namespace, nameof(DepotShipped));
}
