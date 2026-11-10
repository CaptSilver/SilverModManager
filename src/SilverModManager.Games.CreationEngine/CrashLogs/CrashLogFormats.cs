namespace SilverModManager.Games.CreationEngine.CrashLogs;

public static class CrashLogFormats
{
    private const int DetectLength = 512;

    private static readonly ICrashLogFormat[] Known = [new CrashLoggerSseFormat()];

    /// <summary>The format that reads <paramref name="text"/>, or null for an empty or unrecognised file.</summary>
    public static ICrashLogFormat? Detect(string text)
    {
        var head = text.AsSpan(0, Math.Min(text.Length, DetectLength));
        foreach (var format in Known)
        {
            if (format.CanParse(head)) return format;
        }
        return null;
    }
}
