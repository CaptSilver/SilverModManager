using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Models;

[Obsolete("RedMod manifests files should not longer be marked explicitly, they should be identified by path format `{Game}/mods/<redModName>/info.json`")]
[Include<LoadoutFile>]
public partial class RedModInfoFile : IModelDefinition
{
    private static string Namespace => "SilverModManager.Games.RedEngine.Cyberpunk2077.RedModInfoFile";
    
    /// <summary>
    /// The internal name of the mod
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>
    /// The internal version of the mod
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version));
}
