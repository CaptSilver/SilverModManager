using JetBrains.Annotations;
using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;

namespace NexusMods.Abstractions.Loadouts;

/// <summary>
/// A program the user added to run for a game installation, such as an editor or patcher, together with how
/// it should be started.
/// </summary>
[PublicAPI]
public partial class UserTool : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.UserTool";

    /// <summary>
    /// The installation the tool is listed for. It is shared by every loadout of that installation.
    /// </summary>
    public static readonly ReferenceAttribute<GameInstallMetadata> Installation = new(Namespace, nameof(Installation)) { IsIndexed = true };

    /// <summary>
    /// The name shown in the tools list.
    /// </summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    /// <summary>
    /// The absolute path of the program, as a host path.
    /// </summary>
    public static readonly StringAttribute Exe = new(Namespace, nameof(Exe));

    /// <summary>
    /// The command line arguments, as typed: split on spaces, with double quotes grouping words.
    /// </summary>
    public static readonly StringAttribute Arguments = new(Namespace, nameof(Arguments));

    /// <summary>
    /// How the program is started.
    /// </summary>
    public static readonly EnumByteAttribute<ToolRunVia> RunVia = new(Namespace, nameof(RunVia));
}
