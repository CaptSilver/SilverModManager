using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.Witcher.Witcher3.Models;

/// <summary>
/// The Witcher 3 Load Order of a loadout. The order itself is the loadout's group priorities; this only marks the
/// sort order as ours and holds the per-folder switches through <see cref="WitcherModSortOrderItem"/>.
/// </summary>
[PublicAPI]
[Include<SilverModManager.Abstractions.Loadouts.SortOrder>]
public partial class WitcherModSortOrder : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.Witcher.WitcherModSortOrder";

    /// <summary>
    /// Marks an entity as a Witcher 3 sort order, so it can be queried until the database can filter on included models.
    /// Has to be set to true when the model is created.
    /// </summary>
    public static readonly MarkerAttribute Marker = new(Namespace, nameof(Marker));
}
