using SilverModManager.Abstractions.NexusWebApi.Types;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.Networking.NexusWebApi.V1Interop;

/// <summary>
/// Maps between game domains and Game IDs.
/// </summary>
public partial class GameDomainToGameIdMapping : IModelDefinition
{
    private const string Namespace = "SilverModManager.NexusWebApi.GameDomainToGameIdMapping";
    
    /// <summary>
    /// The game's domain.
    /// </summary>
    public static readonly GameDomainAttribute Domain = new(Namespace, nameof(Domain)) { IsIndexed = true };
    
    /// <summary>
    /// The game's ID on Nexus Mods.
    /// </summary>
    public static readonly NexusModsGameIdAttribute GameId = new(Namespace, nameof(GameId)) { IsIndexed = true };
}
