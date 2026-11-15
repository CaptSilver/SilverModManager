namespace SilverModManager.Abstractions.Games.Processes;

/// <summary>
/// The fields of <c>/proc/&lt;pid&gt;/stat</c> this app reads. The command name (field 2) sits in parentheses and may
/// itself hold spaces and ')', so every field is read after the last ')'.
/// </summary>
public readonly record struct ProcStat(char State, int? ParentPid)
{
    /// <summary>Z (exited, waiting to be reaped) and X (dead) are not running.</summary>
    public bool IsAlive => State is not ('Z' or 'X');

    /// <summary>Parses the text of a stat file; null when it has no ')' or nothing after it.</summary>
    public static ProcStat? Parse(string stat)
    {
        var nameEnd = stat.LastIndexOf(')');
        if (nameEnd < 0) return null;

        var fields = stat[(nameEnd + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0 || fields[0].Length == 0) return null;

        var parent = fields.Length > 1 && int.TryParse(fields[1], out var ppid) ? ppid : (int?)null;
        return new ProcStat(fields[0][0], parent);
    }

    /// <summary>
    /// Reads <c>&lt;procRoot&gt;/&lt;pid&gt;/stat</c>; null when the process is gone or its stat cannot be read,
    /// which is normal while a scan races exiting processes.
    /// </summary>
    public static ProcStat? Read(string procRoot, int pid)
    {
        try
        {
            return Parse(File.ReadAllText(Path.Combine(procRoot, pid.ToString(System.Globalization.CultureInfo.InvariantCulture), "stat")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
