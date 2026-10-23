using System.Text;

namespace NexusMods.Backend.OS;

internal partial class LinuxInterop
{
    /// <summary>
    /// Finds the menu entry an AppImage manager (appimaged, AppImageLauncher, Gear Lever) wrote for this AppImage:
    /// a `[Desktop Entry]` whose `Exec` runs <paramref name="appImagePath"/> and whose `MimeType` lists the scheme.
    /// </summary>
    /// <param name="resolvePath">Maps a path to its canonical spelling, so `/home/x` and `/var/home/x` compare equal.</param>
    /// <returns>The file name of the first matching entry, or null.</returns>
    internal static string? FindLauncherDesktopEntry(
        IEnumerable<(string FileName, string Contents)> entries,
        string appImagePath,
        string scheme,
        Func<string, string> resolvePath)
    {
        var wanted = resolvePath(appImagePath);
        var wantedMimeType = $"x-scheme-handler/{scheme}";

        foreach (var (fileName, contents) in entries.OrderBy(e => e.FileName, StringComparer.Ordinal))
        {
            // Our own entry is what we are trying not to duplicate.
            if (fileName == DesktopFile) continue;

            var keys = ReadDesktopEntryGroup(contents);
            if (!keys.TryGetValue("Exec", out var exec) || !keys.TryGetValue("MimeType", out var mimeTypes)) continue;
            if (!mimeTypes.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(wantedMimeType)) continue;

            var executable = GetExecutable(exec);
            if (executable is not null && resolvePath(executable) == wanted) return fileName;
        }

        return null;
    }

    /// <summary>
    /// Resolves symlinks in every component of the path, so differently spelled paths to one file compare equal.
    /// Components that don't exist are kept as they are.
    /// </summary>
    internal static string ResolveRealPath(string path)
    {
        const int maxLinks = 40;
        var pending = new LinkedList<string>(Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries));
        var resolved = "/";
        var followed = 0;

        while (pending.First is { } node)
        {
            pending.RemoveFirst();
            var next = Path.Combine(resolved, node.Value);

            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            string? target;
            try { target = info.LinkTarget; }
            catch (IOException) { target = null; }

            if (target is null)
            {
                resolved = next;
                continue;
            }

            if (++followed > maxLinks) return Path.GetFullPath(path);

            // A relative target is relative to the directory that holds the link.
            var absoluteTarget = Path.GetFullPath(target, resolved);
            var parts = absoluteTarget.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = parts.Length - 1; i >= 0; i--) pending.AddFirst(parts[i]);
            resolved = "/";
        }

        return resolved;
    }

    /// <summary>Reads the key/value pairs of the `[Desktop Entry]` group only; other groups such as actions are skipped.</summary>
    private static Dictionary<string, string> ReadDesktopEntryGroup(string contents)
    {
        var keys = new Dictionary<string, string>(StringComparer.Ordinal);
        var inDesktopEntry = false;

        foreach (var rawLine in contents.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line[0] == '[')
            {
                inDesktopEntry = line == "[Desktop Entry]";
                continue;
            }

            if (!inDesktopEntry) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            keys.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }

        return keys;
    }

    /// <summary>
    /// The program an `Exec` line runs, skipping a leading `env` and its `NAME=value` assignments.
    /// </summary>
    private static string? GetExecutable(string exec)
    {
        var arguments = SplitExecArguments(exec);
        var index = 0;

        if (index < arguments.Count && arguments[index] == "env")
        {
            index++;
            while (index < arguments.Count && (arguments[index].StartsWith('-') || IsEnvironmentAssignment(arguments[index]))) index++;
        }

        return index < arguments.Count ? arguments[index] : null;
    }

    private static bool IsEnvironmentAssignment(string argument)
    {
        var equals = argument.IndexOf('=');
        return equals > 0 && !argument.AsSpan(0, equals).ContainsAny('/', '"', ' ');
    }

    // The string-level escapes (`\s`, `\n`, `\t`, `\r`, `\\`) are decoded first, then the quoting rule applies: double
    // quotes group an argument and inside them a backslash escapes the next character. That order is why our own
    // EscapeDesktopExecFilePath writes a quoted space as `\s` and a quoted backslash as four. A space decoded from
    // `\s` never splits arguments, even unquoted, since nobody writes `\s` to mean a separator.
    private static List<string> SplitExecArguments(string exec)
    {
        var decoded = new List<(char Value, bool Escaped)>(exec.Length);
        for (var i = 0; i < exec.Length; i++)
        {
            if (exec[i] != '\\' || i + 1 == exec.Length)
            {
                decoded.Add((exec[i], false));
                continue;
            }

            var escaped = exec[i + 1] switch
            {
                's' => ' ',
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '\\' => '\\',
                _ => (char?)null,
            };

            if (escaped is null)
            {
                // Not a string escape: keep the backslash for the quoting rule.
                decoded.Add(('\\', false));
                continue;
            }

            // A decoded backslash still escapes the next character inside quotes, so it is not marked.
            decoded.Add((escaped.Value, escaped.Value != '\\'));
            i++;
        }

        var arguments = new List<string>();
        var current = new StringBuilder();
        var started = false;
        var quoted = false;

        for (var i = 0; i < decoded.Count; i++)
        {
            var (c, escaped) = decoded[i];

            if (quoted && c == '\\' && i + 1 < decoded.Count)
            {
                current.Append(decoded[++i].Value);
            }
            else if (c == '"' && !escaped)
            {
                quoted = !quoted;
                started = true;
            }
            else if (!quoted && !escaped && (c == ' ' || c == '\t'))
            {
                if (started) arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started) arguments.Add(current.ToString());
        return arguments;
    }
}
