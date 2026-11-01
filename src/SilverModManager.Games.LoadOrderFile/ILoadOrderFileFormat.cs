using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// How one game writes its mod order down: which files make a folder of the mod folder a mod, and what the order file
/// says for a list of mods. One implementation per file format, shared by every game that writes it.
/// </summary>
public interface ILoadOrderFileFormat
{
    /// <summary>
    /// The name the order file lists for the mod that the file at <paramref name="fileInModFolder"/> (a path below the
    /// game's mod folder) proves is there, or null when the file proves no mod is there.
    /// </summary>
    string? ModOf(RelativePath fileInModFolder);

    /// <summary>The file's content for <paramref name="mods"/>, the mod the game loads first at the start and the winner last.</summary>
    string Render(IReadOnlyList<string> mods);
}
