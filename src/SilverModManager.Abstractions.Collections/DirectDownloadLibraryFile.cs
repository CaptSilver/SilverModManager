using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Abstractions.Collections;

/// <summary>
/// A direct downloaded file from a collection
/// </summary>
[Include<LocalFile>]
public partial class DirectDownloadLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Collections.DirectDownloadLibraryFile";

    /// <summary>
    /// A user-friendly name of the file.
    /// </summary>
    public static readonly StringAttribute LogicalFileName = new(Namespace, nameof(LogicalFileName));
}
