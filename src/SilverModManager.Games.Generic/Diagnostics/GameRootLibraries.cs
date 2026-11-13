using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NexusMods.Paths;
using SilverModManager.Abstractions.Games;
using SilverModManager.Sdk;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Diagnostics;

/// <summary>Whether the game loads a DLL it finds in its own folder.</summary>
public enum RootLoad : byte
{
    /// <summary>Named by an executable in the folder (import or delay-load table) or a proxy name, and nothing in Wine puts its own copy first.</summary>
    Loaded,

    /// <summary>Named, but Wine ships a builtin of that name and no override puts the native file first.</summary>
    ShadowedByWine,

    /// <summary>No executable in the folder names it: an orphan, unless the game loads it by a string at run time.</summary>
    NotByName,
}

/// <param name="Load">What happens to the file.</param>
/// <param name="OverrideThatWouldLoad">The Wine override that would make the native file load, such as <c>version=n,b</c>; set for <see cref="RootLoad.ShadowedByWine"/> only.</param>
public sealed record RootLoadVerdict(RootLoad Load, string? OverrideThatWouldLoad);

/// <summary>
/// Answers, for a DLL sitting in a game's executable folder, whether the game loads it. Windows loads a DLL from the
/// application folder before the system one when an executable names it, so the executables' import tables decide;
/// mod loaders also use proxy names (<see cref="ModLoaderPaths.ProxyDllNames"/>). Under Wine a builtin of the same
/// name wins unless an override puts native first, which is why the prefix matters.
/// </summary>
public sealed class GameRootLibraries(INativeImageReader images, ILogger<GameRootLibraries>? logger = null)
{
    /// <summary>
    /// The DLLs Proton copies its own DXVK/VKD3D builds over and sets to native-first, so a file of one of these
    /// names in the game folder loads without an override.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtonNativeByDefault = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "d3d8.dll", "d3d9.dll", "d3d10core.dll", "d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll",
    };

    private readonly ConcurrentDictionary<string, FolderImports> _folders = new();
    private readonly ConcurrentDictionary<string, BuiltinList> _builtins = new();

    /// <summary>
    /// Judges <paramref name="fileName"/> in <paramref name="folder"/>. <paramref name="prefix"/> is the game's Wine
    /// prefix, null for an install that does not run under Wine, which has Windows load semantics and no builtins.
    /// </summary>
    public async ValueTask<RootLoadVerdict> HowLoaded(AbsolutePath folder, string fileName, ILinuxCompatabilityDataProvider? prefix, CancellationToken token)
    {
        var imported = ImportedNames(folder);
        var byProxyName = ModLoaderPaths.ProxyDllNames.Contains(fileName, StringComparer.OrdinalIgnoreCase);
        if (!byProxyName && !imported.Contains(fileName)) return new RootLoadVerdict(RootLoad.NotByName, null);

        if (prefix is null || !BuiltinNames(prefix).Contains(fileName)) return new RootLoadVerdict(RootLoad.Loaded, null);
        if (ProtonNativeByDefault.Contains(fileName)) return new RootLoadVerdict(RootLoad.Loaded, null);

        var bare = Path.GetFileNameWithoutExtension(fileName);
        var overrides = await prefix.GetWineDllOverrides(token);
        // Wine applies an entry for the DLL's own name before the "*" entry that covers every DLL.
        var nativeFirst = overrides
            .Where(o => string.Equals(Path.GetFileNameWithoutExtension(o.DllName), bare, StringComparison.OrdinalIgnoreCase))
            .Concat(overrides.Where(o => o.DllName == "*"))
            .Select(o => o.PrefersNative)
            .FirstOrDefault();
        return nativeFirst
            ? new RootLoadVerdict(RootLoad.Loaded, null)
            : new RootLoadVerdict(RootLoad.ShadowedByWine, $"{bare}=n,b");
    }

    private HashSet<string> ImportedNames(AbsolutePath folder)
    {
        var executables = Directory.Exists(folder.ToString())
            ? new DirectoryInfo(folder.ToString()).EnumerateFiles("*.exe").OrderBy(f => f.FullName, StringComparer.Ordinal).ToArray()
            : [];
        var signature = string.Join('|', executables.Select(f => $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));

        if (_folders.TryGetValue(folder.ToString(), out var cached) && cached.Signature == signature) return cached.Names;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var exe in executables)
            {
                var read = images.ReadImports(FileSystem.Shared.FromUnsanitizedFullPath(exe.FullName));
                names.UnionWith(read.Libraries);
                names.UnionWith(read.DelayLoaded);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // One unreadable executable leaves the folder judged by the proxy names alone. Not cached: a file locked
            // by a running game or a permission fix does not change the size or time the cache is keyed by.
            logger?.LogTrace(e, "Could not read the imports of an executable in `{Folder}`", folder);
            return [];
        }

        _folders[folder.ToString()] = new FolderImports(signature, names);
        return names;
    }

    private HashSet<string> BuiltinNames(ILinuxCompatabilityDataProvider prefix)
    {
        var system32 = prefix.WinePrefixDirectoryPath.Combine("drive_c/windows/system32").ToString();
        if (!Directory.Exists(system32)) return [];

        var stamp = Directory.GetLastWriteTimeUtc(system32).Ticks;
        if (_builtins.TryGetValue(system32, out var cached) && cached.Stamp == stamp) return cached.Names;

        var names = new HashSet<string>(
            Directory.EnumerateFiles(system32).Select(f => Path.GetFileName(f)), StringComparer.OrdinalIgnoreCase);
        _builtins[system32] = new BuiltinList(stamp, names);
        return names;
    }

    private sealed record FolderImports(string Signature, HashSet<string> Names);

    private sealed record BuiltinList(long Stamp, HashSet<string> Names);
}
