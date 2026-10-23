using JetBrains.Annotations;
using NexusMods.Abstractions.Loadouts;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;

namespace NexusMods.Games.Larian.BaldursGate3.Models;

/// <summary>
/// What the game's <c>modsettings.lsx</c> says about a pak it installed itself: the <c>ModuleShortDesc</c> entry,
/// kept so the entry can be written back as the game had it.
/// </summary>
/// <remarks>
/// Every attribute is optional: an item can exist before the entry has been read.
/// </remarks>
[PublicAPI]
[Include<GameInstalledItem>]
public partial class Bg3ModuleEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Games.BaldursGate3.Bg3ModuleEntry";

    public static readonly StringAttribute Folder = new(Namespace, nameof(Folder)) { IsOptional = true };
    public static readonly StringAttribute Uuid = new(Namespace, nameof(Uuid)) { IsOptional = true };
    public static readonly StringAttribute Version64 = new(Namespace, nameof(Version64)) { IsOptional = true };
    public static readonly StringAttribute PublishHandle = new(Namespace, nameof(PublishHandle)) { IsOptional = true };
    public static readonly StringAttribute Md5 = new(Namespace, nameof(Md5)) { IsOptional = true };
    public static readonly StringAttribute ModuleName = new(Namespace, nameof(ModuleName)) { IsOptional = true };
}
