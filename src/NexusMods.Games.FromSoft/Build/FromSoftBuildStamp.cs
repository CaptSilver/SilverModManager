using NexusMods.MnemonicDB.Abstractions.Attributes;
using NexusMods.MnemonicDB.Abstractions.Models;
using NexusMods.Sdk.Games;

namespace NexusMods.Games.FromSoft.Build;

/// <summary>
/// The build of a FromSoft game an installation had when it was first launched through the app. A later build that
/// differs from it is reported in the Health Check.
/// </summary>
public partial class FromSoftBuildStamp : IModelDefinition
{
    private const string Namespace = "SilverModManager.FromSoft.BuildStamp";

    /// <summary>The installation the stamp is for. There is at most one stamp per installation.</summary>
    public static readonly ReferenceAttribute<GameInstallMetadata> Installation = new(Namespace, nameof(Installation)) { IsIndexed = true };

    /// <summary>The file version of the game's executable when stamped.</summary>
    public static readonly StringAttribute Exe = new(Namespace, nameof(Exe));

    /// <summary>Steam's build id when stamped.</summary>
    public static readonly StringAttribute SteamBuildId = new(Namespace, nameof(SteamBuildId));

    /// <summary>When the stamp was recorded.</summary>
    public static readonly TimestampAttribute RecordedAt = new(Namespace, nameof(RecordedAt));
}
