using NexusMods.Paths;

namespace SilverModManager.Games.LoadOrderFile;

/// <summary>
/// Kingdom Come: Deliverance's <c>mod_order.txt</c>: the folder name of each mod, one per line, in load order. A mod
/// further down overrides the ones above it. A folder is a mod when it holds a manifest, a pak or a cfg file
/// (Vortex's check for the game).
/// </summary>
public sealed class ModOrderTxtFormat : ILoadOrderFileFormat
{
    private static readonly string[] ModFileExtensions = [".pak", ".cfg", ".manifest"];

    public string? ModOf(RelativePath fileInModFolder)
    {
        var parts = fileInModFolder.Path.Split('/');
        if (parts.Length < 2) return null;

        return ModFileExtensions.Any(extension => parts[^1].EndsWith(extension, StringComparison.OrdinalIgnoreCase)) ? parts[0] : null;
    }

    public string Render(IReadOnlyList<string> mods) => string.Join('\n', mods);

    public IReadOnlyList<string> Listed(string fileText)
        => fileText.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
}
