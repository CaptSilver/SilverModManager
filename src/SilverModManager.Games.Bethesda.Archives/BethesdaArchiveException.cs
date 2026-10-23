namespace SilverModManager.Games.Bethesda.Archives;

/// <summary>
/// An archive is malformed, truncated, or of a kind this library does not read.
/// </summary>
public class BethesdaArchiveException : Exception
{
    public BethesdaArchiveException(string message) : base(message) { }

    public BethesdaArchiveException(string message, Exception inner) : base(message, inner) { }

    internal static BethesdaArchiveException Truncated(ArchiveKind kind, long offset, long wanted) =>
        new($"{kind} archive is truncated at offset {offset}: expected {wanted} more byte(s)");

    internal static BethesdaArchiveException Invalid(ArchiveKind kind, long offset, string what) =>
        new($"{kind} archive is invalid at offset {offset}: {what}");
}
