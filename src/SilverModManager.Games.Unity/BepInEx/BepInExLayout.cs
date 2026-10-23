using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.BepInEx;

/// <summary>Two files of one archive resolve to the same place, so one would overwrite the other.</summary>
public sealed class BepInExLayoutException(string message) : Exception(message);

/// <summary>
/// Where the files of a BepInEx package go. The rules follow r2modman's plugin installer so a package lands where
/// its author tested it:
/// <list type="bullet">
/// <item>A folder named plugins, patchers, core or monomod is matched at any depth; everything below it keeps its
/// subfolders and goes to BepInEx/&lt;folder&gt;/&lt;package&gt;/.</item>
/// <item>config is matched the same way but has no package folder, because BepInEx reads
/// BepInEx/config/&lt;guid&gt;.cfg directly.</item>
/// <item>A file outside any such folder is routed by extension (.mm.dll to monomod, everything else to plugins)
/// and flattened to its file name, as r2modman queues each file of an unmatched folder on its own.</item>
/// </list>
/// Paths are matched without regard to case. This type does not know the loader pack; see <see cref="IsLoaderPack"/>.
/// </summary>
public sealed class BepInExLayout
{
    private sealed record FolderRule(string Folder, bool HasPackageFolder);

    private static readonly FolderRule[] FolderRules =
    [
        new(BepInExFiles.Plugins, true),
        new(BepInExFiles.Patchers, true),
        new(BepInExFiles.Core, true),
        new(BepInExFiles.Monomod, true),
        new(BepInExFiles.Config, false),
    ];

    // Longest extension first, so ".mm.dll" is tried before ".dll".
    private static readonly (string Extension, string Folder)[] ExtensionRules =
    [
        (BepInExFiles.MonomodExtension, BepInExFiles.Monomod),
        (BepInExFiles.DllExtension, BepInExFiles.Plugins),
    ];

    /// <summary>Maps each archive path to its place in the game folder.</summary>
    /// <param name="packageFolder">The package's folder name, see <see cref="BepInExPackageFolder"/>.</param>
    /// <exception cref="BepInExLayoutException">Two paths resolve to the same target, or a path has a "." or ".." segment.</exception>
    public static IReadOnlyDictionary<RelativePath, GamePath> Route(IEnumerable<RelativePath> paths, string packageFolder)
    {
        var result = new Dictionary<RelativePath, GamePath>();
        var sources = new Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            var target = RouteOne(path, packageFolder);
            var key = target.Path.ToString();
            if (sources.TryGetValue(key, out var first))
                throw new BepInExLayoutException($"'{first}' and '{path}' both install to '{key}'");

            sources.Add(key, path);
            result.Add(path, target);
        }

        return result;
    }

    /// <summary>
    /// Whether the archive is a BepInEx loader pack: the doorstop proxy, doorstop_config.ini and BepInEx/core/BepInEx.Preloader.dll
    /// under one root folder, or all three at the archive root (<paramref name="root"/> is then empty).
    /// </summary>
    public static bool IsLoaderPack(IEnumerable<RelativePath> paths, out string root)
    {
        var all = paths.Select(p => p.ToString().Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in all.Where(p => Path.GetFileName(p).Equals(BepInExFiles.DoorstopProxy, StringComparison.OrdinalIgnoreCase)))
        {
            var directory = candidate[..^BepInExFiles.DoorstopProxy.Length].TrimEnd('/');
            // Only the pack's own root folder is allowed above the files; a deeper proxy DLL is something else.
            if (directory.Contains('/')) continue;

            var prefix = directory.Length == 0 ? "" : directory + "/";
            if (!all.Contains(prefix + BepInExFiles.DoorstopConfig)) continue;
            if (!all.Contains($"{prefix}{BepInExFiles.Root}/{BepInExFiles.Core}/{BepInExFiles.Preloader}")) continue;

            root = directory;
            return true;
        }

        root = "";
        return false;
    }

    private static GamePath RouteOne(RelativePath path, string packageFolder)
    {
        var parts = path.ToString().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var file = parts[^1];

        // Archive paths come from the mod, and the target is a plain join onto the game folder.
        if (parts.Any(p => p is "." or ".."))
            throw new BepInExLayoutException($"'{path}' has a '.' or '..' segment and cannot be installed");

        // Only folders can match; the last part is the file, so a file called "plugins" is not a folder.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var rule = FolderRules.FirstOrDefault(r => r.Folder.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (rule is null) continue;

            var rest = string.Join('/', parts.Skip(i + 1));
            return rule.HasPackageFolder
                ? Target(rule.Folder, packageFolder, rest)
                : new GamePath(LocationId.Game, $"{BepInExFiles.Root}/{rule.Folder}/{rest}");
        }

        var folder = ExtensionRules.FirstOrDefault(r => file.EndsWith(r.Extension, StringComparison.OrdinalIgnoreCase)).Folder
                     ?? BepInExFiles.Plugins;
        return Target(folder, packageFolder, file);
    }

    private static GamePath Target(string folder, string packageFolder, string rest) =>
        new(LocationId.Game, $"{BepInExFiles.Root}/{folder}/{packageFolder}/{rest}");
}
