using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Manifests;

/// <summary>Reads the file a mod folder carries to say what the mod is and where it loads, in one game's format.</summary>
public interface IModManifestReader
{
    /// <summary>Where the manifest sits, relative to the mod's folder.</summary>
    RelativePath ManifestPath { get; }

    /// <summary>The manifest in <paramref name="bytes"/>, or null when it is unreadable or names no mod.</summary>
    ModManifest? Read(byte[] bytes);
}
