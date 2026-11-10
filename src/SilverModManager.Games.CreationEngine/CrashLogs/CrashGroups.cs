namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>Every log that shares one <see cref="CrashSignature"/>. <see cref="Representative"/> is the report of <see cref="Newest"/>.</summary>
public sealed record CrashGroup(CrashSignature Signature, int Count, CrashLogFile Newest, CrashReport Representative, DateTime FirstSeen, DateTime LastSeen);

public static class CrashGroups
{
    /// <summary>Collapses reports with one signature into a group each, the group with the newest log first.</summary>
    public static IReadOnlyList<CrashGroup> Of(IEnumerable<(CrashLogFile File, CrashReport Report)> reports)
    {
        var groups = new Dictionary<CrashSignature, CrashGroup>();
        foreach (var (file, report) in reports)
        {
            var signature = CrashSignature.Of(report);
            var written = file.LastWriteUtc;
            groups[signature] = groups.TryGetValue(signature, out var group)
                ? Merge(group, file, report)
                : new CrashGroup(signature, 1, file, report, written, written);
        }
        return groups.Values.OrderByDescending(g => g.LastSeen).ToList();
    }

    private static CrashGroup Merge(CrashGroup group, CrashLogFile file, CrashReport report)
    {
        var written = file.LastWriteUtc;
        var newer = written >= group.Newest.LastWriteUtc;
        return group with
        {
            Count = group.Count + 1,
            Newest = newer ? file : group.Newest,
            Representative = newer ? report : group.Representative,
            FirstSeen = written < group.FirstSeen ? written : group.FirstSeen,
            LastSeen = written > group.LastSeen ? written : group.LastSeen,
        };
    }
}
