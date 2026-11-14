using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// A library file that was downloaded from a mod.io mod. The pinned hash is <see cref="LibraryFile.Sha256"/> and
/// mod.io's own checksum is <see cref="LibraryFile.Md5"/>.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class ModIoLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.ModIo";

    // The first attribute declared decides whether an entity counts as this model (IsValid, TryGetAs, All), so it
    // must be one every row has.

    /// <summary>
    /// mod.io's id for the game the mod belongs to. Mod and file ids are only unique inside a game.
    /// </summary>
    public static readonly UInt32Attribute GameId = new(Namespace, nameof(GameId));

    /// <summary>
    /// The mod's id.
    /// </summary>
    public static readonly UInt32Attribute ModId = new(Namespace, nameof(ModId)) { IsIndexed = true };

    /// <summary>
    /// The id of the modfile, the number mod.io's API lists as the file's <c>id</c>.
    /// </summary>
    public static readonly UInt32Attribute FileId = new(Namespace, nameof(FileId)) { IsIndexed = true };

    /// <summary>
    /// The version text the modfile carried when it was downloaded; empty when the author left it out.
    /// </summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version));

    /// <summary>
    /// The mod's last update time (Unix seconds) when the file was downloaded. The update check compares it with the
    /// mod's current one.
    /// </summary>
    public static readonly Int64Attribute DateUpdated = new(Namespace, nameof(DateUpdated));
}
