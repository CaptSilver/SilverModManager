using SilverModManager.Games.CreationEngine.CrashLogs;

namespace SilverModManager.Games.CreationEngine.Tests.CrashLogs;

/// <summary>
/// Real CrashLoggerSSE logs, trimmed by whole-line deletes and kept byte-for-byte (the folder is <c>-text</c> in
/// .gitattributes) because the logs mix CRLF and LF line endings and the parser has to cope with both.
/// </summary>
internal static class Fixtures
{
    public static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "CrashLogs", "Fixtures", name);

    public static string Text(string name) => File.ReadAllText(Path(name));

    public static CrashReport Parse(string name) => new CrashLoggerSseFormat().Parse(Text(name));
}
