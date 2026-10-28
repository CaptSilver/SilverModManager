using JetBrains.Annotations;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.Witcher.Witcher3.Models;

/// <summary>
/// One Mods folder in a <see cref="WitcherModSortOrder"/>. Its place in the order comes from the priority of the
/// groups that supply it, so the inherited index is bookkeeping only.
/// </summary>
[PublicAPI]
[Include<SortOrderItem>]
public partial class WitcherModSortOrderItem : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.Witcher.WitcherModSortOrderItem";

    /// <summary>
    /// The folder's name under Mods, in the spelling it was first listed in. Compare it ignoring case.
    /// </summary>
    public static readonly StringAttribute FolderName = new(Namespace, nameof(FolderName)) { IsOptional = true };

    /// <summary>
    /// Whether the folder is switched on in mods.settings.
    /// </summary>
    public static readonly BooleanAttribute IsEnabled = new(Namespace, nameof(IsEnabled)) { IsOptional = true };
}
