using SilverModManager.Games.Generic.Simple;
using SilverModManager.Games.LoadOrderFile.Manifests;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Names a mod's folder after the id in its manifest, the name the game's order file lists it under, so the folder and
/// the file agree. A game that cannot list a mod without an id sets <see cref="RequireName"/> and the archive is
/// refused instead of being named after itself.
/// </summary>
/// <param name="reader">Reads the manifest.</param>
/// <param name="gameName">The game, for the refusal.</param>
/// <param name="idField">What the manifest calls the id (<c>packageId</c>), for the refusal.</param>
public sealed class ManifestIdFolderName(IModManifestReader reader, string gameName, string idField) : IModFolderNameFromMarker
{
    public bool RequireName { get; init; }

    public string RefusalReason => $"The archive's {reader.ManifestPath.FileName} has no {idField}, so {gameName} could not list the mod";

    public string? FolderName(byte[] markerFile)
        => reader.Read(markerFile)?.Id is { } id && ManifestModIdFolderName.IsFolderName(id) ? id : null;
}
