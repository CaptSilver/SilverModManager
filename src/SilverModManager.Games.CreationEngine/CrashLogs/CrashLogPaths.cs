using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>
/// Turns a string out of a crash log into the game path it names. The log prints the same kinds of file under
/// several spellings (<c>Plants\X.nif</c>, <c>Data/Meshes/X.nif</c>, <c>TEXTURES\cubemaps\Y.dds</c>), so the extension
/// decides what a string is, never the field it was printed in: a <c>File:</c> line can hold a mesh and a relevant
/// object's parenthetical can hold a plugin.
/// </summary>
public static class CrashLogPaths
{
    private const string DataFolder = "Data/";
    private const string MeshesFolder = "meshes/";
    private const string TexturesFolder = "textures/";
    private const string ScriptExtenderPlugins = "Data/SKSE/Plugins/";

    /// <summary>The game path a crash log string names, or null when the extension is not one the game loads from Data.</summary>
    public static GamePath? Classify(string text)
    {
        var path = text.Trim().Replace('\\', '/').TrimStart('/');
        if (path.StartsWith(DataFolder, StringComparison.OrdinalIgnoreCase)) path = path[DataFolder.Length..];
        if (path.Length == 0) return null;

        string? relative;
        if (HasExtension(path, ".nif")) relative = WithPrefix(path, MeshesFolder);
        else if (HasExtension(path, ".dds")) relative = WithPrefix(path, TexturesFolder);
        else if (IsPluginName(path)) relative = path;
        else if (HasExtension(path, ".dll")) return new GamePath(LocationId.Game, ScriptExtenderPlugins + path[(path.LastIndexOf('/') + 1)..]);
        else relative = null;

        return relative is null ? null : new GamePath(LocationId.Game, DataFolder + relative);
    }

    public static bool IsMesh(GamePath path) => HasExtension(path, ".nif");

    public static bool IsTexture(GamePath path) => HasExtension(path, ".dds");

    public static bool IsPlugin(GamePath path) => IsPluginName(path.FileName.ToString());

    public static bool IsDll(GamePath path) => HasExtension(path, ".dll");

    private static string WithPrefix(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : prefix + path;

    private static bool IsPluginName(string name) =>
        HasExtension(name, ".esp") || HasExtension(name, ".esm") || HasExtension(name, ".esl");

    private static bool HasExtension(GamePath path, string extension) => HasExtension(path.FileName.ToString(), extension);

    private static bool HasExtension(string name, string extension) => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
}
