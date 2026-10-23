using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Models;

/// <summary>
/// Represents the RedMod load order
/// </summary>
[PublicAPI]
[Include<Abstractions.Loadouts.SortOrder>]
public partial class RedModSortOrder : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.RedEngine.Cyberpunk2077.RedModSortOrder";
    
    /// <summary>
    /// Marker attribute for querying the model, while waiting for advanced db queries
    /// Needs to be explicitly set to true on new model creation
    /// </summary>
    public static readonly MarkerAttribute Marker = new(Namespace, nameof(Marker)); 
}
