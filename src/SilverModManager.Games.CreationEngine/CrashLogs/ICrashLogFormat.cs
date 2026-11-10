namespace SilverModManager.Games.CreationEngine.CrashLogs;

/// <summary>A crash logger's file format.</summary>
public interface ICrashLogFormat
{
    /// <summary>Whether the start of a log is this logger's. CrashLoggerSSE: the third line starts with <c>CrashLoggerSSE v</c>.</summary>
    bool CanParse(ReadOnlySpan<char> firstLines);

    /// <summary>Reads a whole log. A log that is cut off yields a partial report; it does not throw.</summary>
    CrashReport Parse(string text);
}
