using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// A library file that was downloaded from a GameBanana mod page. The pinned hash is <see cref="LibraryFile.Sha256"/>
/// and GameBanana's own checksum is <see cref="LibraryFile.Md5"/>.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class GameBananaLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.GameBanana";

    // The first attribute declared decides whether an entity counts as this model (IsValid, TryGetAs, All), so it
    // must be one every row has.

    /// <summary>
    /// The id of the mod page, the number in <c>https://gamebanana.com/mods/{id}</c>.
    /// </summary>
    public static readonly Int64Attribute ModId = new(Namespace, nameof(ModId)) { IsIndexed = true };

    /// <summary>
    /// The id of the file on the mod page, the number in <c>https://gamebanana.com/dl/{id}</c>.
    /// </summary>
    public static readonly Int64Attribute FileId = new(Namespace, nameof(FileId)) { IsIndexed = true };

    /// <summary>
    /// GameBanana's id for the game the mod is listed under.
    /// </summary>
    public static readonly Int32Attribute GameId = new(Namespace, nameof(GameId));

    /// <summary>
    /// The file name GameBanana lists for the file.
    /// </summary>
    public static readonly StringAttribute ListedFileName = new(Namespace, nameof(ListedFileName));

    /// <summary>
    /// The mod's last update time (Unix seconds) when the file was downloaded. Zero when the page had no update
    /// stamp. The update check compares it with the page's current one.
    /// </summary>
    public static readonly Int64Attribute DateUpdated = new(Namespace, nameof(DateUpdated));

    /// <summary>
    /// The version text the mod page carried when the file was downloaded. Absent when the page had none, which is
    /// common: the row then shows when the page was last updated instead.
    /// </summary>
    public static readonly StringAttribute ListedVersion = new(Namespace, nameof(ListedVersion)) { IsOptional = true };
}
