using System.Text.RegularExpressions;
using NexusMods.Paths;
using NexusMods.Platform.Proton.Steam;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.Steam;

namespace NexusMods.Platform.Proton;

/// <summary>
/// Finds Proton builds the way Steam does and applies the per-app compat tool choice from <c>config.vdf</c>.
/// </summary>
public sealed partial class ProtonResolver(IEnvironmentReader environment) : IProtonResolver
{
    private const string ExtraToolPathsVariable = "STEAM_EXTRA_COMPAT_TOOLS_PATHS";

    [GeneratedRegex(@"^Proton (\d+)\.(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionedDirectoryName();

    /// <inheritdoc/>
    public IReadOnlyList<ProtonBuild> ListBuilds(AbsolutePath steamInstallPath)
        => Scan(steamInstallPath).Select(found => found.Build).ToArray();

    private FoundBuild[] Scan(AbsolutePath steamInstallPath)
    {
        var builds = new Dictionary<string, FoundBuild>(StringComparer.Ordinal);

        void Add(ProtonBuild build, bool isOfficial) => builds.TryAdd(build.Name, new FoundBuild(build, isOfficial));

        // Steam prefers user-installed tools over its own, so they are added first and win a name clash.
        foreach (var root in CompatToolRoots(steamInstallPath))
        {
            foreach (var dir in SubDirectories(root)) AddCustom(dir, Add);
        }

        foreach (var library in Libraries(steamInstallPath))
        {
            foreach (var dir in SubDirectories(library.Combine("steamapps/common")))
            {
                if (dir.FileName.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)) AddOfficial(dir, Add);
            }
        }

        return builds.Values
            .OrderBy(found => found.Build.Directory.FileName, NaturalComparer.Instance)
            .ToArray();
    }

    /// <inheritdoc/>
    public ProtonResolution ResolveForApp(AbsolutePath steamInstallPath, uint appId)
    {
        var builds = Scan(steamInstallPath);
        var mappings = ReadMappings(steamInstallPath);

        var mapped = Lookup(mappings, appId) ?? Lookup(mappings, 0);
        if (mapped is not null)
        {
            var found = builds.FirstOrDefault(b => b.Build.Name.Equals(mapped, StringComparison.OrdinalIgnoreCase));
            return found.Build is not null
                ? ProtonResolution.Resolved(found.Build)
                : ProtonResolution.Failed($"Steam is set to run this game with the compatibility tool '{mapped}', which is not installed.");
        }

        var newest = NewestStable(builds);
        return newest is not null
            ? ProtonResolution.Resolved(newest)
            : ProtonResolution.Failed("No Proton build is installed. Install one through Steam or put one in compatibilitytools.d.");
    }

    private static string? Lookup(IReadOnlyDictionary<uint, string> mappings, uint appId)
        => mappings.TryGetValue(appId, out var name) && name.Length > 0 ? name : null;

    private static ProtonBuild? NewestStable(FoundBuild[] builds)
    {
        // Valve's own builds are what Steam falls back to; custom ones only count when there are none.
        var stable = builds.Where(found => !IsMovingTarget(found.Build)).ToArray();
        return stable.LastOrDefault(found => found.IsOfficial).Build ?? stable.LastOrDefault().Build;
    }

    private static bool IsMovingTarget(ProtonBuild build)
        => build.Name.Contains("experimental", StringComparison.OrdinalIgnoreCase)
           || build.Name.Contains("hotfix", StringComparison.OrdinalIgnoreCase);

    private static void AddCustom(AbsolutePath dir, Action<ProtonBuild, bool> add)
    {
        if (!dir.Combine("proton").FileExists) return;

        var name = ReadInternalToolName(dir.Combine("compatibilitytool.vdf")) ?? dir.FileName;
        add(new ProtonBuild(name, dir), false);
    }

    private static void AddOfficial(AbsolutePath dir, Action<ProtonBuild, bool> add)
    {
        if (!dir.Combine("proton").FileExists) return;
        add(new ProtonBuild(OfficialMappingName(dir.FileName), dir), true);
    }

    /// <summary>
    /// Steam stores Valve's builds in <c>CompatToolMapping</c> as <c>proton_9</c>, <c>proton_experimental</c>
    /// and so on, never under the directory name.
    /// </summary>
    private static string OfficialMappingName(string directoryName)
    {
        if (directoryName.Contains("Experimental", StringComparison.OrdinalIgnoreCase)) return "proton_experimental";
        if (directoryName.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)) return "proton_hotfix";

        var match = VersionedDirectoryName().Match(directoryName);
        if (!match.Success) return directoryName;

        // 5.13 and 6.3 were named proton_513 and proton_63; from 7.0 on only the major version is used.
        var major = int.Parse(match.Groups[1].Value);
        return major >= 7 ? $"proton_{major}" : $"proton_{major}{match.Groups[2].Value}";
    }

    private static string? ReadInternalToolName(AbsolutePath vdf)
    {
        var tools = VdfFile.Read(vdf)?["compat_tools"];
        return tools?.Children.FirstOrDefault()?.Name;
    }

    private IEnumerable<AbsolutePath> CompatToolRoots(AbsolutePath steamInstallPath)
    {
        yield return steamInstallPath.Combine("compatibilitytools.d");

        var extra = environment.Get(ExtraToolPathsVariable);
        if (string.IsNullOrEmpty(extra)) yield break;

        foreach (var entry in extra.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            // A relative entry would resolve against whatever the working directory happens to be.
            if (!entry.StartsWith('/')) continue;
            yield return steamInstallPath.FileSystem.FromUnsanitizedFullPath(entry);
        }
    }

    private static IEnumerable<AbsolutePath> Libraries(AbsolutePath steamInstallPath)
    {
        var seen = new HashSet<AbsolutePath> { steamInstallPath };
        yield return steamInstallPath;

        var folders = VdfFile.Read(steamInstallPath.Combine("steamapps/libraryfolders.vdf"));
        if (folders is null) yield break;

        foreach (var library in folders.Children)
        {
            var path = library["path"].Value;
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/')) continue;

            var libraryPath = steamInstallPath.FileSystem.FromUnsanitizedFullPath(path);
            if (seen.Add(libraryPath)) yield return libraryPath;
        }
    }

    private static Dictionary<uint, string> ReadMappings(AbsolutePath steamInstallPath)
    {
        var result = new Dictionary<uint, string>();
        var root = VdfFile.Read(steamInstallPath.Combine("config/config.vdf"));
        var mapping = root?["Software"]["Valve"]["Steam"]["CompatToolMapping"];
        if (mapping is null) return result;

        foreach (var app in mapping.Children)
        {
            if (!uint.TryParse(app.Name, out var id)) continue;
            var name = app["name"].Value;
            if (name is not null) result[id] = name;
        }

        return result;
    }

    private static IEnumerable<AbsolutePath> SubDirectories(AbsolutePath dir)
        => dir.DirectoryExists() ? dir.EnumerateDirectories(recursive: false) : [];

    /// <summary>
    /// A build plus where it was found, since only Valve's own builds count for the fallback.
    /// </summary>
    private readonly record struct FoundBuild(ProtonBuild Build, bool IsOfficial);

    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            x ??= "";
            y ??= "";
            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    var xe = i;
                    while (xe < x.Length && char.IsAsciiDigit(x[xe])) xe++;
                    var ye = j;
                    while (ye < y.Length && char.IsAsciiDigit(y[ye])) ye++;

                    // Compare as numbers without parsing, so absurdly long digit runs cannot overflow.
                    var xd = x.AsSpan(i, xe - i).TrimStart('0');
                    var yd = y.AsSpan(j, ye - j).TrimStart('0');
                    if (xd.Length != yd.Length) return xd.Length.CompareTo(yd.Length);
                    var cmp = xd.CompareTo(yd, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;

                    i = xe;
                    j = ye;
                }
                else
                {
                    var cmp = char.ToLowerInvariant(x[i]).CompareTo(char.ToLowerInvariant(y[j]));
                    if (cmp != 0) return cmp;
                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
