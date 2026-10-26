using SilverModManager.Abstractions.Cli;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SilverModManager.Platform.Proton.Launch;

/// <summary>
/// One installation's launch lock as found on disk.
/// </summary>
/// <param name="Store">The store part of the lock's name, such as <c>steam</c>.</param>
/// <param name="StoreId">The store's id for the game, as it appears in the lock's name.</param>
/// <param name="Pid">Process id of the holder, or null when the lock is held but no state has been written yet.</param>
/// <param name="Loadout">Name of the loadout being launched, or null when there is no state.</param>
/// <param name="Phase">How far the launch has got, or null when there is no state.</param>
/// <param name="IsLeftover">True when nobody holds the lock and the state file is all that remains of a launch that died.</param>
public sealed record LaunchStatusEntry(string Store, string StoreId, int? Pid, string? Loadout, LaunchPhase? Phase, bool IsLeftover);

/// <summary>
/// What <c>status</c> reports: the launches that hold or have left a lock, and the virtual file system mounts.
/// </summary>
public sealed partial record LaunchStatusReport(IReadOnlyList<LaunchStatusEntry> Launches, IReadOnlyList<string> Mounts)
{
    /// <summary>
    /// The file system type the virtual file system mounts with, as <c>/proc/self/mountinfo</c> names it.
    /// </summary>
    public const string VfsFileSystemType = "fuse.silvermod";

    private const string LockPrefix = "launch-";

    /// <summary>
    /// Reads every <c>launch-*.lock</c> under <paramref name="runtimeDir"/> and the mounts in
    /// <paramref name="mountInfoPath"/>. Both are passed in so tests never look at the real ones.
    /// </summary>
    public static LaunchStatusReport Read(string runtimeDir, string mountInfoPath) => new(ReadLaunches(runtimeDir), ReadMounts(mountInfoPath));

    private static List<LaunchStatusEntry> ReadLaunches(string runtimeDir)
    {
        var entries = new List<LaunchStatusEntry>();
        var folder = InstallationLock.DirectoryFor(runtimeDir);
        if (!Directory.Exists(folder)) return entries;

        foreach (var path in Directory.EnumerateFiles(folder, $"{LockPrefix}*.lock").Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path)[LockPrefix.Length..];
            var split = name.IndexOf('-');
            var (store, storeId) = split < 0 ? (name, "") : (name[..split], name[(split + 1)..]);

            var launchLock = new InstallationLock(path);
            InstallationLockState? state;
            try
            {
                state = launchLock.ReadState();
            }
            catch (InvalidDataException)
            {
                // A half-written file still leaves the question of who holds the lock.
                state = null;
            }

            if (state is not null)
                entries.Add(new LaunchStatusEntry(store, storeId, state.Pid, state.Loadout, state.Phase, state.IsLeftover));
            else if (launchLock.Probe())
                entries.Add(new LaunchStatusEntry(store, storeId, null, null, null, IsLeftover: false));
        }

        return entries;
    }

    private static List<string> ReadMounts(string mountInfoPath)
    {
        var mounts = new List<string>();
        foreach (var line in File.ReadLines(mountInfoPath))
        {
            // "<id> <parent> <dev> <root> <mount point> <options> [optional fields] - <type> <source> <super options>"
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0) continue;

            var before = line[..separator].Split(' ');
            var after = line[(separator + 3)..].Split(' ');
            if (before.Length < 5 || after[0] != VfsFileSystemType) continue;

            mounts.Add(Unescape(before[4]));
        }

        return mounts;
    }

    // mountinfo writes space, tab, newline and backslash in a path as three-digit octal escapes.
    private static string Unescape(string path) => OctalEscape().Replace(path, match => ((char)Convert.ToInt32(match.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();

    public string ToText()
    {
        var builder = new StringBuilder();
        if (Launches.Count == 0) builder.AppendLine("No game is running through SilverModManager.");

        foreach (var launch in Launches)
        {
            builder.Append(launch.Store).Append(' ').Append(launch.StoreId).Append(": ");
            if (launch.Pid is null)
                builder.Append("locked, no details recorded yet");
            else
                builder.Append(CultureInfo.InvariantCulture, $"pid {launch.Pid}, loadout '{launch.Loadout}', phase {PhaseName(launch.Phase)}");
            if (launch.IsLeftover) builder.Append(" (leftover: that launch died without cleaning up)");
            builder.AppendLine();
        }

        if (Mounts.Count == 0)
        {
            builder.AppendLine("Mounts: none");
        }
        else
        {
            builder.AppendLine("Mounts:");
            foreach (var mount in Mounts) builder.Append("  ").AppendLine(mount);
        }

        return builder.ToString();
    }

    public string ToJson() => JsonSerializer.Serialize(new
    {
        launches = Launches.Select(launch => new
        {
            store = launch.Store,
            storeId = launch.StoreId,
            pid = launch.Pid,
            loadout = launch.Loadout,
            phase = launch.Phase is null ? null : PhaseName(launch.Phase),
            leftover = launch.IsLeftover,
        }),
        mounts = Mounts,
    }, CliJson.Options);

    private static string PhaseName(LaunchPhase? phase) => phase.ToString()!.ToLowerInvariant();
}
