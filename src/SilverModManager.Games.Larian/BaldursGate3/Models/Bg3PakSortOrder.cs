using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.Larian.BaldursGate3.Models;

/// <summary>
/// The pak load order of a Baldur's Gate 3 loadout.
/// </summary>
[PublicAPI]
[Include<SilverModManager.Abstractions.Loadouts.SortOrder>]
public partial class Bg3PakSortOrder : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.BaldursGate3.Bg3PakSortOrder";

    /// <summary>
    /// Marks an entity as a pak sort order, so it can be queried until the database can filter on included models.
    /// Has to be set to true when the model is created.
    /// </summary>
    public static readonly MarkerAttribute Marker = new(Namespace, nameof(Marker));

    /// <summary>
    /// The order was first filled from the game's own <c>modsettings.lsx</c>, rather than built from the loadout alone.
    /// </summary>
    public static readonly BooleanAttribute SeededFromGameFile = new(Namespace, nameof(SeededFromGameFile));
}
