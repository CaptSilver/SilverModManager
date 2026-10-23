using JetBrains.Annotations;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.Larian.BaldursGate3.Models;

/// <summary>
/// One pak in a <see cref="Bg3PakSortOrder"/>.
/// </summary>
[PublicAPI]
[Include<SortOrderItem>]
public partial class Bg3PakSortOrderItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.BaldursGate3.Bg3PakSortOrderItem";

    /// <summary>
    /// The pak's file name in the Mods folder, extension included. Compare it ignoring case.
    /// </summary>
    public static readonly StringAttribute PakName = new(Namespace, nameof(PakName));

    /// <summary>
    /// Whether the pak is switched on in the load order. Ignored for a pak the game installed itself, whose
    /// group decides.
    /// </summary>
    public static readonly BooleanAttribute IsEnabled = new(Namespace, nameof(IsEnabled));
}
