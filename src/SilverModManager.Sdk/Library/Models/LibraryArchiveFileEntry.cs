using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Represents a file inside a library archive.
/// </summary>
/// <remarks>
///     Please do not create alternatives to LibraryArchiveFileEntry that can store
///     archived files without updating the GC.
/// </remarks>
[PublicAPI]
[Include<LibraryFile>]
public partial class LibraryArchiveFileEntry : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.LibraryArchiveFileEntry";

    /// <summary>
    /// Path to the file inside the archive.
    /// </summary>
    public static readonly RelativePathAttribute Path = new(Namespace, nameof(Path));

    /// <summary>
    /// Reference to the parent archive that contains this file.
    /// </summary>
    public static readonly ReferenceAttribute<LibraryArchive> Parent = new(Namespace, nameof(Parent));

    /// <summary>
    /// Whether the file is a member extracted from a library archive rather than a file the library holds itself.
    /// </summary>
    public static bool IsMember(IDb db, EntityId file) => Load(db, file).IsValid();
}
