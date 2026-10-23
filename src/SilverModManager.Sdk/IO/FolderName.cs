namespace NexusMods.Sdk.IO;

/// <summary>
/// Checks and repairs a name that is about to become one folder below a game or mods folder, when the name comes from
/// an archive, a library item or a manifest. Wine reads these folders under Windows rules, so the rules are Windows':
/// no <c>&lt;&gt;:"/\|?*</c>, no control characters, and a trailing dot or space is dropped, which would turn
/// <c>...</c> into the parent folder.
/// </summary>
public static class FolderName
{
    private static readonly char[] Forbidden = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static bool IsForbidden(char c) => char.IsControl(c) || Array.IndexOf(Forbidden, c) >= 0;

    /// <summary>True when <paramref name="name"/> can be used as one folder name exactly as it is.</summary>
    public static bool IsUsable(string name)
        => !string.IsNullOrWhiteSpace(name)
           && !name.EndsWith('.')
           && !name.EndsWith(' ')
           && !name.Any(IsForbidden);

    /// <summary>
    /// Makes <paramref name="name"/> usable by replacing forbidden characters with '_' and trimming trailing dots and
    /// spaces. Null when nothing is left, so a caller can fall back to another source for the name or refuse.
    /// </summary>
    public static string? Sanitize(string name)
    {
        var replaced = string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = IsForbidden(source[i]) ? '_' : source[i];
        });

        var trimmed = replaced.TrimEnd('.', ' ');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}
