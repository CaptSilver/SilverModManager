using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile.Naming;

/// <summary>Names the mod or mods a folder holds the way the order file lists them.</summary>
public interface IExternalModNamer
{
    /// <summary>The names for <paramref name="modFolder"/>; empty when the folder is no mod. Some games keep one name per descriptor file, so a folder may hold several.</summary>
    IReadOnlyList<string> Names(AbsolutePath modFolder);
}
