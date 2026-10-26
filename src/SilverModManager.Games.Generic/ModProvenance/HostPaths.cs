using System.Text.RegularExpressions;

namespace SilverModManager.Games.Generic.ModProvenance;

/// <summary>Turns the paths another manager wrote (often Windows paths seen through Wine) into paths on this machine.</summary>
internal static partial class HostPaths
{
    private const int MaxLinkHops = 40;

    [GeneratedRegex(@"^(?<drive>[A-Za-z]):/(?<rest>.*)$")]
    private static partial Regex DriveRegex();

    /// <summary>
    /// <paramref name="value"/> as a host path, or null when it names a drive the reader cannot place. <c>Z:</c> is the
    /// host's root under Wine; <c>C:</c> is the <c>drive_c</c> folder of the prefix <paramref name="anchor"/> sits in.
    /// A relative value is taken relative to <paramref name="anchor"/>.
    /// </summary>
    public static string? FromWindows(string? value, string anchor)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var path = value.Trim().Replace("\\\\", "\\").Replace('\\', '/');
        if (DriveRegex().Match(path) is { Success: true } drive)
        {
            var rest = drive.Groups["rest"].Value;
            if (char.ToUpperInvariant(drive.Groups["drive"].Value[0]) == 'Z') return Path.GetFullPath("/" + rest);

            var marker = anchor.IndexOf("/drive_c/", StringComparison.Ordinal);
            if (marker < 0 && anchor.EndsWith("/drive_c", StringComparison.Ordinal)) marker = anchor.Length - "/drive_c".Length;
            return marker < 0 ? null : Path.GetFullPath(Path.Combine(anchor[..(marker + "/drive_c".Length)], rest));
        }

        return Path.GetFullPath(path, anchor);
    }

    /// <summary>
    /// Whether <paramref name="gamePath"/> is <paramref name="gameFolder"/> or the folder right above it (a manager
    /// stores either the install folder or the one holding the executable), after following links. Anything further up,
    /// such as a drive root, holds every game and says nothing.
    /// </summary>
    public static bool IsGameFolderOrItsParent(string gamePath, string gameFolder)
    {
        var path = RealPath(gamePath).TrimEnd('/');
        var game = RealPath(gameFolder).TrimEnd('/');
        return path == game || (path.Length > 0 && Path.GetDirectoryName(game) == path);
    }

    /// <summary>The path with every link on the way followed, so <c>/home/x</c> and <c>/var/home/x</c> compare equal.</summary>
    internal static string RealPath(string path, int hops = 0)
    {
        var current = "/";
        foreach (var segment in Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? target = null;
            try
            {
                target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (target is not null && hops < MaxLinkHops) current = RealPath(target.FullName, hops + 1);
        }

        return current;
    }
}
