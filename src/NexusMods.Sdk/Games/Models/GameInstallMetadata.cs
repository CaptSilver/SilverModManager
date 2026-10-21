using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.BuiltInEntities;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Loadouts;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Sdk.Games;

/// <summary>
/// Metadata about a game installation. This model exists so that parts of the system can reference a single
/// MnemonicDb model, instead of a tuple of domain/store/path
/// </summary>
public partial class GameInstallMetadata : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.GameMetadata";

    // The first attribute is the one All() and the other model-wide lookups key on, so it has to be
    // one every row has: the id attributes below are optional.

    /// <summary>
    /// The name of the store the game is from
    /// </summary>
    public static readonly GameStoreAttribute Store = new(Namespace, nameof(Store));

    /// <summary>
    /// The path to the game's installation directory.
    /// </summary>
    public static readonly StringAttribute Path = new(Namespace, nameof(Path)) { IsIndexed = true };

    /// <summary>
    /// The game's own id. Installations and the loadouts on them are found by this, not by the Nexus id:
    /// not every game has a Nexus id, and some games share one (Skyrim SE and Skyrim VR).
    /// </summary>
    /// <remarks>
    /// Optional only because rows written before this attribute existed lack it. A migration fills it in for
    /// the games the app knew then; a row it cannot place stays without one and matches no game.
    /// </remarks>
    public static readonly GameIdAttribute Game = new(Namespace, nameof(Game)) { IsIndexed = true, IsOptional = true };

    /// <summary>
    /// The game's id on Nexus Mods, for the parts of the app that talk to Nexus. Absent for a game that
    /// has none.
    /// </summary>
    public static readonly NexusModsGameIdAttribute GameId = new(Namespace, nameof(GameId)) { IsIndexed = true, IsOptional = true };

    // TODO: remove
    /// <summary>
    /// User friendly name for the game.
    /// May be referred to from diagnostics or otherwise.
    /// </summary>
    [Obsolete("to be removed")]
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>
    /// The last applied loadout to this game state
    /// </summary>
    public static readonly ReferenceAttribute<Loadout> LastSyncedLoadout = new(Namespace, nameof(LastSyncedLoadout)) { IsOptional = true };

    /// <summary>
    /// The 'AsOf' transaction ID of the last applied loadout
    /// </summary>
    public static readonly ReferenceAttribute<Transaction> LastSyncedLoadoutTransaction = new(Namespace, nameof(LastSyncedLoadoutTransaction)) { IsOptional = true };

    /// <summary>
    /// The 'AsOf' transaction ID of the initial disk state when the game folder was first indexed
    /// </summary>
    public static readonly ReferenceAttribute<Transaction> InitialDiskStateTransaction = new(Namespace, nameof(InitialDiskStateTransaction)) { IsOptional = true };

    /// <summary>
    /// The last scanned disk state transaction ID
    /// </summary>
    public static readonly ReferenceAttribute<Transaction> LastScannedDiskStateTransaction = new(Namespace, nameof(LastScannedDiskStateTransaction)) { IsOptional = true };
}
