using NexusMods.Paths;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// How a simple game's mod archives are laid out, as data. Destinations are relative to the definition's
/// <see cref="SimpleGameDefinition.ModRoot"/>.
/// </summary>
public sealed record SimpleInstallRules
{
    /// <summary>
    /// Archives the game turns away, tried before every other rule. For mods that only work with a program the app does
    /// not run, so installing their files would leave the player with half a mod and no hint why.
    /// </summary>
    public IReadOnlyList<SimpleRefusalRule> Refusals { get; init; } = [];

    /// <summary>
    /// Archives with a marker file, tried first: the folder holding the marker is the mod, installed to the rule's
    /// destination. Needed where the folder shape alone would send the same files somewhere else.
    /// </summary>
    public IReadOnlyList<SimpleMarkerRule> MarkerRules { get; init; } = [];

    /// <summary>
    /// Archives with a descriptor file of a given extension, tried after the marker rules: the folder holding the
    /// descriptor is the mod, installed in a folder named after the descriptor.
    /// </summary>
    public IReadOnlyList<SimpleDescriptorRule> DescriptorRules { get; init; } = [];

    /// <summary>Known folder names and file extensions, matched in the order given.</summary>
    public IReadOnlyList<SimpleInstallTarget> Targets { get; init; } = [];

    /// <summary>
    /// Files sent to a folder by their extension, tried after the folder targets: for games whose mods are loose files
    /// of several kinds that each belong somewhere else, whatever folder the archive keeps them in.
    /// </summary>
    public IReadOnlyList<SimpleExtensionRoute> ExtensionRoutes { get; init; } = [];

    /// <summary>
    /// With <see cref="ExtensionRoutes"/>: files whose extension no route names are dropped instead of making the
    /// archive unsupported. For games whose plugin installs only the kinds it knows and ignores the rest.
    /// </summary>
    public bool LeaveOutUnroutedFiles { get; init; }

    /// <summary>
    /// The archive is laid out like the mod folder and goes in unchanged, whatever it holds. For games that take any
    /// file in their folder and publish no layout to match against. Tried last, because it accepts every archive.
    /// </summary>
    public bool InstallAsIs { get; init; }

    /// <summary>Where an <see cref="InstallAsIs"/> archive goes, below the definition's <see cref="SimpleGameDefinition.ModRoot"/>.</summary>
    public RelativePath InstallAsIsDestination { get; init; } = RelativePath.Empty;
}

/// <summary>
/// An archive holding a file named <see cref="FileName"/> or a folder named <see cref="FolderName"/>, at any depth and in
/// any case, is refused with <see cref="Reason"/>, which is shown to the player as it is.
/// </summary>
public sealed record SimpleRefusalRule
{
    public string? FileName { get; init; }

    public string? FolderName { get; init; }

    public required string Reason { get; init; }
}

/// <summary>A folder of a game that archives can be laid out for.</summary>
public sealed record SimpleInstallTarget
{
    public required RelativePath Destination { get; init; }

    /// <summary>Names an archive may call this folder, which are dropped from the installed path.</summary>
    public IReadOnlyList<RelativePath> KnownSourceFolderNames { get; init; } = [];

    /// <summary>
    /// Folders that only appear directly inside this one. The folder holding one of them is the archive's root, so a
    /// wrapper folder above it is dropped and the files beside it come along. Unlike a source folder name, the
    /// matched folder keeps its name.
    /// </summary>
    public IReadOnlyList<RelativePath> KnownSubfolderNames { get; init; } = [];

    /// <summary>Extensions of files that belong directly in this folder.</summary>
    public IReadOnlyList<Extension> KnownValidFileExtensions { get; init; } = [];

    /// <summary>Extensions of files that are not part of the mod (readmes, screenshots).</summary>
    public IReadOnlyList<Extension> FileExtensionsToDiscard { get; init; } = [];
}

/// <summary>An archive with a file named <see cref="MarkerFile"/> in its mod folder is installed to <see cref="Destination"/>.</summary>
public sealed record SimpleMarkerRule
{
    public required string MarkerFile { get; init; }

    public required RelativePath Destination { get; init; }

    /// <summary>
    /// The mod goes in a folder of its own below <see cref="Destination"/>, for games that load every folder there as
    /// one mod. The folder is named after the one that holds the marker file, or after the archive when the marker
    /// is at its top level.
    /// </summary>
    public bool InFolderNamedAfterMod { get; init; }

    /// <summary>
    /// Names the folder from the marker file's content instead, when it gives a name. Only read with
    /// <see cref="InFolderNamedAfterMod"/>.
    /// </summary>
    public IModFolderNameFromMarker? FolderNameFromMarker { get; init; }

    /// <summary>
    /// Extensions that are neither installed nor a reason to decline, so a readme outside the marker's folder does
    /// not make the archive look like a different layout.
    /// </summary>
    public IReadOnlyList<Extension> IgnoredExtensions { get; init; } = [];
}

/// <summary>
/// An archive with a file of <see cref="Extension"/> in its mod folder is installed to <see cref="Destination"/> in a
/// folder named after that file, whatever the archive's own folder is called. For games that tie a mod folder to the
/// name of its descriptor file.
/// </summary>
public sealed record SimpleDescriptorRule
{
    public required Extension Extension { get; init; }

    public required RelativePath Destination { get; init; }

    /// <summary>Extensions that are neither installed nor a reason to decline, so a readme outside the mod's folder does not make the archive look like a different layout.</summary>
    public IReadOnlyList<Extension> IgnoredExtensions { get; init; } = [];
}

/// <summary>
/// Files with one of <see cref="Extensions"/> go to <see cref="Destination"/>, below the definition's mod root. They keep
/// at most the last <see cref="KeepFolders"/> folders of their path in the archive, so a game that only reads a few
/// levels deep gets files it can find, and a game that wants them flat gets that with zero.
/// </summary>
public sealed record SimpleExtensionRoute
{
    public required IReadOnlyList<Extension> Extensions { get; init; }

    public required RelativePath Destination { get; init; }

    public int KeepFolders { get; init; }
}
