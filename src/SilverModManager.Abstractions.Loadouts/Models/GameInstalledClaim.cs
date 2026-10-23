using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Loadouts;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// The user said a game-installed file is theirs. The claim keeps the recognizer from adopting the path
/// again. It is not a <see cref="LoadoutItem"/>, so deleting the group or cleaning the folder leaves it alone.
/// </summary>
[PublicAPI]
public partial class GameInstalledClaim : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.GameInstalledClaim";

    public static readonly ReferenceAttribute<Loadout> Loadout = new(Namespace, nameof(Loadout)) { IsIndexed = true };

    public static readonly GamePathParentAttribute Path = new(Namespace, nameof(Path));

    /// <summary>
    /// What the file was, kept for the undo label: the <see cref="GameInstalledItem.Source"/> of its item.
    /// </summary>
    public static readonly StringAttribute Source = new(Namespace, nameof(Source));

    /// <summary>
    /// The <see cref="GameInstalledItem.SourceId"/> of its item.
    /// </summary>
    public static readonly StringAttribute SourceId = new(Namespace, nameof(SourceId));

    public static readonly TimestampAttribute ClaimedAt = new(Namespace, nameof(ClaimedAt));
}
