using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Games.FileHashes.Models;

public partial class GogManifest : IModelDefinition
{
    private const string Namespace = "NexusMods.Abstractions.Games.FileHashes.GogManifest";
    
    /// <summary>
    /// The (unique) primary key of the manifest.
    /// </summary>
    public static readonly StringAttribute ManifestId = new(Namespace, nameof(ManifestId)) { IsIndexed = true };
    
    /// <summary>
    /// The files in the manifest
    /// </summary>
    public static readonly ReferencesAttribute<PathHashRelation> Files = new(Namespace, nameof(Files));
}
