using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity.MelonLoader;

/// <summary>A mod archive cannot be installed as laid out: two files share a target, or a path climbs out of the game folder.</summary>
public sealed class MelonLoaderLayoutException(string message) : Exception(message);

/// <summary>
/// Where the files of a MelonLoader package go. MelonLoader reads <c>Mods</c>, <c>Plugins</c>, <c>UserLibs</c> and
/// <c>UserData</c> beside the executable, so:
/// <list type="bullet">
/// <item>One of those folders is matched at any depth and keeps its subtree under that name.</item>
/// <item>A loose <c>.dll</c> goes to Mods; a loose <c>.cfg</c>, <c>.json</c> or <c>.ini</c> to UserData.</item>
/// <item>The manifest, readme, icon and changelog at the archive root are Thunderstore's, not the mod's, and are dropped.</item>
/// <item>A mod that ships one DLL per scripting backend (<c>Foo.Il2cpp.dll</c> and <c>Foo.Mono.dll</c>) keeps the one that
/// matches the install: the other would fail to load or load twice.</item>
/// </list>
/// Paths are matched without regard to case. This type does not know the loader pack; see <see cref="IsLoaderPack"/>.
/// </summary>
public static class MelonLoaderLayout
{
    private static readonly string[] ModFolders = [MelonLoaderFiles.Mods, MelonLoaderFiles.Plugins, MelonLoaderFiles.UserLibs, MelonLoaderFiles.UserData];

    private static readonly string[] SettingsExtensions = [".cfg", ".json", ".ini"];

    private static readonly char[] TokenSeparators = ['.', '_', '-'];

    /// <summary>
    /// Maps each archive path to its place in the game folder, or to null when the file is not installed.
    /// </summary>
    /// <param name="runtime">The install's scripting backend, which decides between per-backend builds of one DLL.</param>
    /// <exception cref="MelonLoaderLayoutException">Two paths resolve to the same target, or a path has a "." or ".." segment.</exception>
    public static IReadOnlyDictionary<RelativePath, GamePath?> Route(IEnumerable<RelativePath> paths, UnityRuntime runtime)
    {
        var routes = paths.Select(path => (Path: path, Target: RouteOne(path))).ToList();
        DropOtherBranch(routes, runtime);

        var result = new Dictionary<RelativePath, GamePath?>();
        var sources = new Dictionary<string, RelativePath>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, target) in routes)
        {
            if (target is { } kept)
            {
                var key = kept.Path.ToString();
                if (sources.TryGetValue(key, out var first))
                    throw new MelonLoaderLayoutException($"'{first}' and '{path}' both install to '{key}'");
                sources.Add(key, path);
            }

            result.Add(path, target);
        }

        return result;
    }

    /// <summary>
    /// Whether the archive is the loader pack: <c>version.dll</c> and a <c>MelonLoader</c> folder at one root folder, or at the
    /// archive root (<paramref name="root"/> is then empty).
    /// </summary>
    public static bool IsLoaderPack(IEnumerable<RelativePath> paths, out string root)
    {
        var all = paths.Select(p => p.ToString().Replace('\\', '/')).ToArray();

        foreach (var candidate in all.Where(p => Path.GetFileName(p).Equals(MelonLoaderFiles.Proxy, StringComparison.OrdinalIgnoreCase)))
        {
            var directory = candidate[..^MelonLoaderFiles.Proxy.Length].TrimEnd('/');
            // Only the pack's own root folder is allowed above the files; a deeper proxy DLL is something else.
            if (directory.Contains('/')) continue;

            var folder = (directory.Length == 0 ? "" : directory + "/") + MelonLoaderFiles.Root + "/";
            if (!all.Any(p => p.StartsWith(folder, StringComparison.OrdinalIgnoreCase))) continue;

            root = directory;
            return true;
        }

        root = "";
        return false;
    }

    private static GamePath? RouteOne(RelativePath path)
    {
        var parts = path.ToString().Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var file = parts[^1];

        // Archive paths come from the mod, and the target is a plain join onto the game folder.
        if (parts.Any(p => p is "." or ".."))
            throw new MelonLoaderLayoutException($"'{path}' has a '.' or '..' segment and cannot be installed");

        // Only folders can match; the last part is the file, so a file called "Mods" is not a folder.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var folder = ModFolders.FirstOrDefault(f => f.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (folder is null) continue;

            return new GamePath(LocationId.Game, $"{folder}/{string.Join('/', parts.Skip(i + 1))}");
        }

        if (parts.Length == 1 && ThunderstorePackageMetadata.IsAtRoot(file)) return null;

        var extension = Path.GetExtension(file);
        if (extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)) return new GamePath(LocationId.Game, $"{MelonLoaderFiles.Mods}/{file}");
        if (SettingsExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return new GamePath(LocationId.Game, $"{MelonLoaderFiles.UserData}/{file}");
        return null;
    }

    // Two DLLs that differ only by an Il2Cpp/Mono token are the same mod built for each backend. With a known install
    // the other one goes; with an unknown one either might be right, so both stay.
    private static void DropOtherBranch(List<(RelativePath Path, GamePath? Target)> routes, UnityRuntime runtime)
    {
        if (runtime == UnityRuntime.Unknown) return;

        var branches = routes
            .Select((route, index) => (Index: index, route.Target))
            .Where(r => r.Target is { } target && target.Extension.ToString().Equals(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.Index, Target: r.Target.GetValueOrDefault(), Branch: UnityRuntimeBranch.OfFileName(r.Target.GetValueOrDefault().FileName)))
            .Where(r => r.Branch != UnityRuntime.Unknown)
            .GroupBy(r => (Folder: r.Target.Path.Parent.ToString().ToLowerInvariant(), Stem: StemWithoutBranch(r.Target.FileName)));

        foreach (var group in branches.Where(g => g.Select(r => r.Branch).Distinct().Count() > 1))
        {
            foreach (var other in group.Where(r => r.Branch != runtime))
                routes[other.Index] = (routes[other.Index].Path, null);
        }
    }

    private static string StemWithoutBranch(string fileName)
    {
        var tokens = Path.GetFileNameWithoutExtension(fileName).Split(TokenSeparators)
            .Where(t => !t.Equals("Il2Cpp", StringComparison.OrdinalIgnoreCase) && !t.Equals("Mono", StringComparison.OrdinalIgnoreCase));
        return string.Join('.', tokens).ToLowerInvariant();
    }
}
