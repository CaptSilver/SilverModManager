using JetBrains.Annotations;
using SilverModManager.MnemonicDB.Abstractions.Attributes;
using SilverModManager.MnemonicDB.Abstractions.Models;
using NexusMods.Paths;
using SilverModManager.Sdk.Library;

namespace SilverModManager.Wabbajack.Models;

/// <summary>
/// A library file that is a Wabbajack modlist (a <c>.wabbajack</c> file). The fields are read from the list when it
/// is added, so the library can show it without opening the file again.
/// </summary>
[PublicAPI]
[Include<LibraryFile>]
public partial class WabbajackModlistLibraryFile : IModelDefinition
{
    private const string Namespace = "SilverModManager.Wabbajack.ModlistLibraryFile";

    /// <summary>The list's name.</summary>
    public static readonly StringAttribute Name = new(Namespace, nameof(Name));

    public static readonly StringAttribute Author = new(Namespace, nameof(Author));

    /// <summary>The list's version as the author wrote it.</summary>
    public static readonly StringAttribute Version = new(Namespace, nameof(Version));

    /// <summary>Wabbajack's game enum name, e.g. <c>SkyrimSpecialEdition</c>.</summary>
    public static readonly StringAttribute GameType = new(Namespace, nameof(GameType));

    /// <summary>How many downloads the list needs.</summary>
    public static readonly Int32Attribute ArchiveCount = new(Namespace, nameof(ArchiveCount));

    /// <summary>The summed size of those downloads.</summary>
    public static readonly SizeAttribute ArchiveSize = new(Namespace, nameof(ArchiveSize));

    /// <summary>The summed size of the files the list installs.</summary>
    public static readonly SizeAttribute InstallSize = new(Namespace, nameof(InstallSize));

    public static readonly BooleanAttribute IsNsfw = new(Namespace, nameof(IsNsfw));
}
