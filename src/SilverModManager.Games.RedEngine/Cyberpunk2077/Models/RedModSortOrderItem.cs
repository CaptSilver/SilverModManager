using JetBrains.Annotations;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.RedEngine.Cyberpunk2077.Models;

[PublicAPI]
[Include<SortOrderItem>]
public partial class RedModSortOrderItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.RedEngine.Cyberpunk2077.RedModSortableEntry";
    
    /// <summary>
    /// The identifier used by the game for the RedMod load order
    /// </summary>
    public static readonly RelativePathAttribute RedModFolderName = new(Namespace, nameof(RedModFolderName));
}
