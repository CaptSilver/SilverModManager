using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.Tools;

namespace SilverModManager.Platform.Proton.Tools;

/// <summary>A catalogue tool found on disk: the definition it matched and the install to save.</summary>
public sealed record DetectedTool(ToolDefinition Definition, InstalledTool Installed);

/// <summary>
/// Finds catalogue tools the user already has. Nothing is downloaded: the user points at a folder or a program
/// and the file names are matched against the registered definitions.
/// </summary>
public sealed class KnownToolDetector(IEnumerable<ToolDefinition> definitions)
{
    private readonly ToolDefinition[] _catalogue = definitions.Where(d => d.Id is not null).ToArray();

    /// <summary>
    /// The tools in <paramref name="folder"/> that apply to <paramref name="game"/>, one per matching program, by
    /// file name. Programs in subfolders are not looked at: the LOD tools sit together in one folder.
    /// </summary>
    public IReadOnlyList<DetectedTool> Scan(IGame game, AbsolutePath folder)
    {
        if (folder.DirectoryExists() is false) return [];

        return folder.EnumerateFiles()
            .OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(file => Match(game, file))
            .ToArray();
    }

    /// <summary>
    /// The tools a single picked program can be. Empty when no definition knows its name, which is when the
    /// caller falls back to an ad-hoc definition.
    /// </summary>
    public IReadOnlyList<DetectedTool> Match(IGame game, AbsolutePath exe)
    {
        var name = exe.FileName;
        return _catalogue
            .Where(d => d.AppliesTo(game) && d.ExecutableNames(game).Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(d => new DetectedTool(d, new InstalledTool(exe)))
            .ToArray();
    }
}
