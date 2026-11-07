using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// The switched-off group in the default collection that cleaning the game folder moves unknown files into. The next
/// Apply takes them off disk, and the user can still see them and switch them back on. Marked so it is found by what
/// it is rather than by its name.
/// </summary>
[Include<LoadoutItemGroup>]
public partial class FoundInGameFolderGroup : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.FoundInGameFolderGroup";

    public static readonly MarkerAttribute Marker = new(Namespace, nameof(Marker)) { IsIndexed = true };
}
