using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SilverModManager.App.Mcp;

/// <summary>
/// Reads the warnings and errors out of a SilverModManager log file, whose entries look like
/// <c>00:00:03.300 [ERROR] (Logger) message</c>, with a stack trace on the lines after an entry that has one.
/// </summary>
public static partial class LogReader
{
    [GeneratedRegex(@"^(\d+):(\d{2}):(\d{2})\.(\d+) \[([A-Z]+)\] ")]
    private static partial Regex EntryStart();

    [GeneratedRegex(@"^#+ .* log file - (\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d+)?)")]
    private static partial Regex Header();

    private sealed class Entry(LogLevel level, TimeSpan offset, string firstLine)
    {
        public LogLevel Level { get; } = level;
        public TimeSpan Offset { get; } = offset;
        public List<string> Lines { get; } = [firstLine];
    }

    /// <summary>
    /// The newest <paramref name="limit"/> entries at <paramref name="minLevel"/> or worse, each with its trace. The
    /// log stamps entries with the time since the process started, so <paramref name="since"/> is worked out from the
    /// file's header time and is only as exact as that.
    /// </summary>
    public static string Read(string path, LogLevel minLevel, int limit, TimeSpan? since, DateTime now)
    {
        if (!File.Exists(path)) return $"There is no log file at {path} yet.";

        var newest = new Queue<Entry>();
        var matching = 0;
        DateTime? started = null;
        Entry? current = null;

        // The app has the file open for writing and archives it on its next start, so share with both.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (Header().Match(line) is { Success: true } header)
            {
                if (DateTime.TryParse(header.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) started = time;
                current = null;
                continue;
            }

            if (EntryStart().Match(line) is { Success: true } start)
            {
                current = new Entry(ParseLevel(start.Groups[5].Value), ParseOffset(start), line);
                if (Wanted(current, minLevel, since, started, now))
                {
                    matching++;
                    newest.Enqueue(current);
                    if (newest.Count > limit) newest.Dequeue();
                }
                else
                {
                    current = null;
                }
                continue;
            }

            current?.Lines.Add(line);
        }

        if (matching == 0) return $"No entries at {LevelName(minLevel)} or worse{(since is { } s ? $" in the last {(int)s.TotalMinutes} minutes" : "")}.";

        var text = new StringBuilder();
        if (matching > newest.Count) text.Append($"Showing the newest {newest.Count} of {matching} entries.\n");
        foreach (var entry in newest) text.AppendJoin('\n', entry.Lines).Append('\n');
        return text.ToString();
    }

    private static bool Wanted(Entry entry, LogLevel minLevel, TimeSpan? since, DateTime? started, DateTime now) =>
        // Compared as elapsed time: now - since underflows DateTime for a since longer than the calendar goes back.
        entry.Level >= minLevel && (since is null || started is null || now - (started.Value + entry.Offset) <= since);

    private static TimeSpan ParseOffset(Match start)
    {
        var fraction = start.Groups[4].Value;
        var millis = int.Parse(fraction.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
        return new TimeSpan(0, int.Parse(start.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(start.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(start.Groups[3].Value, CultureInfo.InvariantCulture), millis);
    }

    private static LogLevel ParseLevel(string name) => name switch
    {
        "TRACE" => LogLevel.Trace,
        "DEBUG" => LogLevel.Debug,
        "INFO" => LogLevel.Information,
        "WARN" => LogLevel.Warning,
        "ERROR" => LogLevel.Error,
        _ => LogLevel.Critical,
    };

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        _ => "FATAL",
    };
}
