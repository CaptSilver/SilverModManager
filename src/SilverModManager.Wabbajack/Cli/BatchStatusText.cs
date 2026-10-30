using NexusMods.Paths;
using SilverModManager.Wabbajack.Acquire;
using SilverModManager.Wabbajack.Library;

namespace SilverModManager.Wabbajack.Cli;

/// <summary>The lines <c>wabbajack status</c> and <c>wabbajack download</c> print, kept apart from the verbs so they can be tested without a renderer.</summary>
internal static class BatchStatusText
{
    /// <summary>How a batch stands. <paramref name="plan"/> supplies the archives waiting on the user; it is empty once the acquirer has forgotten it.</summary>
    public static IReadOnlyList<string> Describe(AcquireStatus status, string modlist, ArchivePlan plan)
    {
        List<string> lines =
        [
            status.IsRunning ? $"Downloading '{modlist}': running" : $"Downloading '{modlist}': not running (the last batch has ended)",
            $"Archives: {status.Finished} of {status.Total} done",
            Transferred(status),
        ];

        var counts = PlanCounts.Of(plan);
        if (plan.Rows.Count > 0)
        {
            lines.Add($"Waiting on the browser: {counts.Browser}");
            lines.Add($"Waiting on you (by hand): {counts.Manual}");
        }

        if (status.Failures.Count > 0)
        {
            lines.Add($"Latest failures ({status.Failures.Count}):");
            lines.AddRange(status.Failures.Select(f => $"  {f.Archive}: {f.Reason}"));
        }
        return lines;
    }

    private static string Transferred(AcquireStatus status)
    {
        var bytes = Size.FromLong(status.BytesFetched);
        return status.BytesPerSecond is { } rate
            ? $"Downloaded: {bytes}, on average {Size.FromLong(rate)}/s"
            : $"Downloaded: {bytes}";
    }

    public static string Started(DownloadStart start)
        => start.AlreadyRunning
            ? $"The downloads of '{start.Modlist.Name}' are already running. 'wabbajack status' shows how far they have got."
            : $"Started downloading '{start.Modlist.Name}': {Counts(start.Counts)}. 'wabbajack status' shows how far it has got.";

    public static string Counts(PlanCounts counts)
        => $"{counts.Automatic} to download automatically, {counts.Browser} to fetch in the browser, {counts.Manual} to get by hand, {counts.Have} already there";

    public static string[] ListRow(ModlistInfo modlist, bool downloading)
        => [modlist.HexId, modlist.Name, modlist.Version, modlist.GameType, modlist.ArchiveCount.ToString(), downloading ? "downloading" : ""];
}
