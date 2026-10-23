using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Games.Larian.BaldursGate3.Models;

/// <summary>
/// A note that the app replaced the mod list the game had in <c>modsettings.lsx</c>, kept so the Load Order page
/// can say so.
/// </summary>
[PublicAPI]
public partial class Bg3ModListNotice : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.BaldursGate3.Bg3ModListNotice";

    /// <summary>
    /// The pak sort order whose list replaced the game's.
    /// </summary>
    public static readonly ReferenceAttribute<SilverModManager.Abstractions.Loadouts.SortOrder> SortOrder = new(Namespace, nameof(SortOrder))
    {
        IsIndexed = true,
    };

    /// <summary>
    /// What was replaced, in a sentence the page can show as is.
    /// </summary>
    public static readonly StringAttribute Summary = new(Namespace, nameof(Summary));

    /// <summary>
    /// When the game's list was replaced.
    /// </summary>
    public static readonly TimestampAttribute ReplacedAt = new(Namespace, nameof(ReplacedAt));

    /// <summary>
    /// How many times the game reset its mod list. Absent on notices written before the count existed.
    /// </summary>
    public static readonly Int32Attribute ResetCount = new(Namespace, nameof(ResetCount)) { IsOptional = true };

    /// <summary>
    /// The pak names the app last wrote to the mod list, one per line.
    /// </summary>
    public static readonly StringAttribute LastWrittenPaks = new(Namespace, nameof(LastWrittenPaks)) { IsOptional = true };

    /// <summary>
    /// The pak names written before <see cref="LastWrittenPaks"/>, one per line.
    /// </summary>
    public static readonly StringAttribute PreviousWrittenPaks = new(Namespace, nameof(PreviousWrittenPaks)) { IsOptional = true };
}
