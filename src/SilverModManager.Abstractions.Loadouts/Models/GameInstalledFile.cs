using JetBrains.Annotations;
using NexusMods.Hashing.xxHash3;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Abstractions.Loadouts;

/// <summary>
/// A file in the game folder that belongs to a <see cref="GameInstalledItem"/>. It is a plain
/// <see cref="LoadoutItem"/> and deliberately not a <see cref="LoadoutItemWithTargetPath"/>: the game owns the
/// bytes and the file is never deployed, so it must stay out of the winning-files query.
/// </summary>
[Include<LoadoutItem>]
[PublicAPI]
public partial class GameInstalledFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Loadouts.GameInstalledFile";

    /// <summary>
    /// Where the file is in the game folder.
    /// </summary>
    public static readonly GamePathParentAttribute Path = new(Namespace, nameof(Path)) { IsIndexed = true };

    /// <summary>
    /// The file's hash on disk at the last reconcile.
    /// </summary>
    public static readonly HashAttribute Hash = new(Namespace, nameof(Hash));

    /// <summary>
    /// The file's size on disk at the last reconcile.
    /// </summary>
    public static readonly SizeAttribute Size = new(Namespace, nameof(Size));
}
