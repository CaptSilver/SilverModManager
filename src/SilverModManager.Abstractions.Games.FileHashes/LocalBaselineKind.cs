namespace SilverModManager.Abstractions.Games.FileHashes;

/// <summary>
/// Where the vanilla file list of a <see cref="Models.LocalBaseline"/> came from.
/// </summary>
public enum LocalBaselineKind : byte
{
    /// <summary>Every depot of the installation had a manifest, so the file list is the store's own.</summary>
    DepotManifests = 0,

    /// <summary>Some depots had no manifest on disk; the files of the others come from manifests, the rest are hashed from disk.</summary>
    PartialDepotManifests = 1,

    /// <summary>No manifest was available; the files were hashed from the game directory as it stood.</summary>
    Snapshot = 2,
}
