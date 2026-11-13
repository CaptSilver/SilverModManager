using SilverModManager.Games.Bethesda.LoadOrder.Formats;
using SilverModManager.Games.CreationEngine.PluginLists;
using NexusMods.Paths;

namespace SilverModManager.Games.CreationEngine.OpenMw;

/// <summary>
/// Turns the path values of an openmw.cfg into folders, with the tokens OpenMW's configuration manager replaces
/// (<c>?userdata?</c>, <c>?userconfig?</c>). A value with any other token, or one that is not absolute, names no
/// folder the app can know and gives null.
/// </summary>
public static class OpenMwPaths
{
    private const string UserData = "?userdata?";
    private const string UserConfig = "?userconfig?";

    /// <summary>The text of an openmw.cfg, or null when there is no such file.</summary>
    /// <exception cref="UndecodablePluginListException">The file is not valid UTF-8.</exception>
    public static string? ReadConfig(AbsolutePath file)
    {
        if (!file.FileExists) return null;

        using var stream = file.Read();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new OpenMwConfigFormat().Decode(bytes.ToArray());
    }

    public static AbsolutePath? Resolve(string value, OpenMwConfigRoot root, IFileSystem fileSystem)
    {
        if (TryExpand(value, UserData, root.UserDataFolder, out var expanded)
            || TryExpand(value, UserConfig, root.ConfigFolder, out expanded))
            return expanded;
        if (value.Contains('?') || !value.StartsWith('/')) return null;
        return fileSystem.FromUnsanitizedFullPath(value);
    }

    /// <summary>The data folder OpenMW loads last, which the config's <c>data-local=</c> sets and which defaults to <c>data</c> in the user data folder.</summary>
    public static AbsolutePath DataLocal(string? configText, OpenMwConfigRoot root, IFileSystem fileSystem) =>
        (configText is null ? null : OpenMwConfigValues.Last(configText, "data-local") is { } value ? Resolve(value, root, fileSystem) : null)
        ?? root.UserDataFolder / "data";

    // OpenMW appends what follows the token to the folder, so "?userdata?data" is <user data>/data.
    private static bool TryExpand(string value, string token, AbsolutePath folder, out AbsolutePath? expanded)
    {
        expanded = null;
        if (!value.StartsWith(token, StringComparison.Ordinal)) return false;

        var rest = value[token.Length..].TrimStart('/', '\\');
        expanded = rest.Length == 0 ? folder : folder.Combine(rest);
        return true;
    }
}
