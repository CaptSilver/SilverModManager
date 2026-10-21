using System.Runtime.InteropServices;

namespace NexusMods.Backend.Process;

/// <summary>
/// Finds and signals the processes below a process, by the parent pids in <c>/proc</c>.
/// </summary>
/// <remarks>
/// Entries come and go while the scan runs, so a <c>stat</c> file that cannot be read means the process is
/// gone, not an error.
/// </remarks>
internal static partial class ProcessTree
{
    private const int SigInt = 2;

    /// <summary>
    /// Every process below <paramref name="rootPid"/>, children before grandchildren. The root is not included.
    /// </summary>
    internal static IReadOnlyList<int> Descendants(int rootPid, string procRoot = "/proc")
    {
        var childrenOf = new Dictionary<int, List<int>>();
        foreach (var dir in Directory.EnumerateDirectories(procRoot))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid)) continue;
            if (ReadParent(dir) is not { } parent) continue;

            if (!childrenOf.TryGetValue(parent, out var children)) childrenOf[parent] = children = [];
            children.Add(pid);
        }

        var result = new List<int>();
        var pending = new Queue<int>([rootPid]);
        var seen = new HashSet<int> { rootPid };
        while (pending.TryDequeue(out var pid))
        {
            if (!childrenOf.TryGetValue(pid, out var children)) continue;
            foreach (var child in children.Where(seen.Add))
            {
                result.Add(child);
                pending.Enqueue(child);
            }
        }
        return result;
    }

    /// <summary>
    /// Sends SIGINT to each pid. Pids that <c>kill(2)</c> would read as a process group, or that are this
    /// process, are skipped.
    /// </summary>
    internal static void Interrupt(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            if (pid <= 1 || pid == Environment.ProcessId) continue;
            // A process that exited since the scan fails with ESRCH, which is the outcome we wanted anyway.
            _ = kill(pid, SigInt);
        }
    }

    private static int? ReadParent(string dir)
    {
        string stat;
        try { stat = File.ReadAllText(Path.Combine(dir, "stat")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        // The second field is the name in parentheses, which may itself hold spaces and ')'. The fields after
        // the last ')' are the state and then the parent pid.
        var nameEnd = stat.LastIndexOf(')');
        if (nameEnd < 0 || nameEnd + 2 > stat.Length) return null;
        var fields = stat[(nameEnd + 2)..].Split(' ');
        return fields.Length > 1 && int.TryParse(fields[1], out var parent) ? parent : null;
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int kill(int pid, int signal);
}
