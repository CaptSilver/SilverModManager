using SilverModManager.Abstractions.Loadouts;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Games.LoadOrder;

/// <summary>
/// An entry of an imported order that could not be placed. The rows of one <see cref="Container"/> are
/// replaced on each import and go with the container, so a Health Check card never outlives its collection.
/// </summary>
public partial class ModOrderDroppedEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.ModOrderDroppedEntry";

    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    /// <summary>The collection or Wabbajack group the order was for.</summary>
    public static readonly ReferenceAttribute<LoadoutItemGroup> Container = new(Namespace, nameof(Container)) { IsIndexed = true };

    /// <summary>The name the card prints for the source.</summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source));

    public static readonly StringAttribute Item = new(Namespace, nameof(Item));
    public static readonly StringAttribute Reason = new(Namespace, nameof(Reason));

    /// <summary>The page to fix it on, e.g. "Load Order".</summary>
    public static readonly StringAttribute Page = new(Namespace, nameof(Page));
}
