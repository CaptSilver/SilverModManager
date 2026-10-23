using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;

namespace SilverModManager.Sdk.Library;

/// <summary>
/// Represents a local file in the library.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class LocalFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Library.LocalFile";

    /// <summary>
    /// The original path from where the local file originated from.
    /// </summary>
    public static readonly StringAttribute OriginalPath = new(Namespace, nameof(OriginalPath));
}
