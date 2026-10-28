using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.Witcher.Witcher3.Models;

/// <summary>
/// A note that the app replaced a mods.settings that differed from the Load Order, kept so the Health Check can say
/// so. A model of its own, not an include of the sort order: writing it during Apply must not count as a change to
/// the sort order that a move on the page checks against.
/// </summary>
[PublicAPI]
public partial class WitcherModsSettingsNotice : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.Witcher.WitcherModsSettingsNotice";

    /// <summary>
    /// The Witcher 3 sort order whose list replaced the file on disk.
    /// </summary>
    public static readonly ReferenceAttribute<SilverModManager.Abstractions.Loadouts.SortOrder> SortOrder = new(Namespace, nameof(SortOrder))
    {
        IsIndexed = true,
    };

    /// <summary>
    /// What differed, in a sentence the card can show as is.
    /// </summary>
    public static readonly StringAttribute Summary = new(Namespace, nameof(Summary));

    /// <summary>
    /// When the file on disk was replaced.
    /// </summary>
    public static readonly TimestampAttribute ReplacedAt = new(Namespace, nameof(ReplacedAt));
}
