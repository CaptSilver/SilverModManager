using System.Text;
using NexusMods.Sdk.Games;

namespace NexusMods.Platform.Proton;

/// <summary>
/// Finds processes by reading <c>/proc</c>. The root is a parameter so tests can point it at a fixture tree.
/// </summary>
/// <remarks>
/// Entries come and go while the scan runs, and other users' processes hide their environment, so a file
/// that cannot be read means "this process says nothing", never an error.
/// </remarks>
public sealed class ProcProcessTracker(string procRoot) : IProcessTracker
{
    /// <inheritdoc />
    public IReadOnlyList<TrackedProcess> FindGameProcesses(GameInstallation installation)
    {
        var gameDir = RealPath(installation.Locations[LocationId.Game].Path.ToString());
        var prefixes = PrefixesOf(installation.LocatorResult);
        var compatDataDirs = prefixes
            .Where(p => Path.GetFileName(p) == "pfx")
            .Select(Path.GetDirectoryName)
            .OfType<string>()
            .ToHashSet();

        return Scan().Where(p => RunsFromGameDir(p) || UsesPrefix(p) || WineRunsFromGameDir(p)).Select(p => p.Process).ToList();

        bool RunsFromGameDir(Candidate p) => p.ExePath is { } exe && IsInFolder(exe, gameDir);

        bool UsesPrefix(Candidate p)
        {
            var environment = p.Environment();
            return (environment.TryGetValue("WINEPREFIX", out var wine) && prefixes.Contains(RealPath(wine)))
                   || (environment.TryGetValue("STEAM_COMPAT_DATA_PATH", out var compat) && compatDataDirs.Contains(RealPath(compat)));
        }

        // Wine processes show up as the loader binary, with the Windows program as a Z: argument.
        bool WineRunsFromGameDir(Candidate p) => p.Arguments().Any(a => HostPathOfZDrive(a) is { } host && IsInFolder(RealPath(host), gameDir));
    }

    /// <inheritdoc />
    public IReadOnlyList<TrackedProcess> FindByName(string name)
    {
        return Scan().Where(p => HasName(p, name)).Select(p => p.Process).ToList();
    }

    /// <inheritdoc />
    public bool IsSteamRunning() => FindByName("steam").Count > 0;

    /// <inheritdoc />
    public TrackedProcess? FindReaperForApp(uint appId)
    {
        var wanted = $"AppId={appId}";
        return Scan()
            .Where(p => p.Process.Name == "reaper" && p.Arguments().Contains(wanted))
            .Select(p => p.Process)
            .FirstOrDefault();
    }

    private static bool HasName(Candidate p, string name)
    {
        if (string.Equals(p.Process.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        if (p.ExePath is { } exe && string.Equals(Path.GetFileName(exe), name, StringComparison.OrdinalIgnoreCase)) return true;

        // The kernel keeps 15 characters of the name, so Wine's own executable name is only in the command line.
        var args = p.Arguments();
        return args.Count > 0 && (FileNameOf(args[0]) == name.ToLowerInvariant() || args.Skip(1).Any(a => LooksLikeWindowsPath(a) && FileNameOf(a) == name.ToLowerInvariant()));
    }

    private IEnumerable<Candidate> Scan()
    {
        IEnumerable<string> entries;
        try { entries = Directory.EnumerateDirectories(procRoot).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var dir in entries.OrderBy(d => d, StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;

            var comm = ReadText(Path.Combine(dir, "comm"));
            if (comm is null) continue;

            yield return new Candidate(new TrackedProcess(pid, comm.TrimEnd('\n')), dir);
        }
    }

    private static HashSet<string> PrefixesOf(GameLocatorResult result)
    {
        var prefixes = new HashSet<string>();
        if (result.WinePrefixPath is { } wine) prefixes.Add(RealPath(wine.ToString()));
        if (result.Compatibility is { } layer) prefixes.Add(RealPath(layer.Prefix.ToString()));
        if (result.LinuxCompatabilityDataProvider is { } provider) prefixes.Add(RealPath(provider.WinePrefixDirectoryPath.ToString()));
        return prefixes;
    }

    private static string? HostPathOfZDrive(string argument)
    {
        if (argument.Length < 3 || char.ToUpperInvariant(argument[0]) != 'Z' || argument[1] != ':') return null;
        return argument[2..].Replace('\\', '/');
    }

    private static bool LooksLikeWindowsPath(string argument) => argument.Contains('\\') || (argument.Length > 1 && argument[1] == ':');

    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['/', '\\']);
        return (cut < 0 ? path : path[(cut + 1)..]).ToLowerInvariant();
    }

    private static bool IsInFolder(string path, string folder)
    {
        return path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Resolves symlinks in every component, like <c>realpath</c>. <c>/home</c> is a symlink on ostree systems
    /// while <c>/proc/&lt;pid&gt;/exe</c> always reports the resolved path. A path that does not exist is
    /// returned as written, because a process can still name it.
    /// </summary>
    internal static string RealPath(string path)
    {
        var pending = new Stack<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        for (var hops = 0; pending.Count > 0;)
        {
            var next = Path.Combine(current, pending.Pop());
            string? target;
            try { target = new FileInfo(next).LinkTarget; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { target = null; }

            if (target is null)
            {
                current = next;
                continue;
            }

            if (++hops > 40) return Path.GetFullPath(path);
            var resolved = Path.GetFullPath(Path.Combine(current, target));
            foreach (var part in resolved.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse()) pending.Push(part);
            current = "/";
        }
        return current;
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private sealed class Candidate(TrackedProcess process, string dir)
    {
        public TrackedProcess Process => process;

        public string? ExePath
        {
            get
            {
                try { return new FileInfo(Path.Combine(dir, "exe")).LinkTarget; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
            }
        }

        public IReadOnlyList<string> Arguments() => ReadEntries("cmdline");

        public Dictionary<string, string> Environment()
        {
            var result = new Dictionary<string, string>();
            foreach (var entry in ReadEntries("environ"))
            {
                var split = entry.IndexOf('=');
                if (split > 0) result[entry[..split]] = entry[(split + 1)..];
            }
            return result;
        }

        private IReadOnlyList<string> ReadEntries(string file)
        {
            try { return Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(dir, file))).Split('\0', StringSplitOptions.RemoveEmptyEntries); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }
    }
}
