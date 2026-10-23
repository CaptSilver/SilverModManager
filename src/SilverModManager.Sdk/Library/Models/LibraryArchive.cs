using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Represents an archive in the library.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class LibraryArchive : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.LibraryArchive";

    /// <summary>
    /// Marker.
    /// </summary>
    public static readonly MarkerAttribute Archive = new(Namespace, nameof(Archive));

    /// <summary>
    /// Back-reference to all files inside the archive.
    /// </summary>
    public static readonly BackReferenceAttribute<LibraryArchiveFileEntry> Children = new(LibraryArchiveFileEntry.Parent);
}
