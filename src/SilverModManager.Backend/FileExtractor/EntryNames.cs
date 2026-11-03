using System.Text;

namespace SilverModManager.Backend.FileExtractor;

/// <summary>
/// How the name of an archive entry becomes the name it gets on disk. A zip stores a name as code page 437
/// unless general purpose bit 11 says UTF-8, and 7z hands an unflagged non-UTF-8 name over as its raw
/// bytes, which .NET can neither open nor rename. Windows and macOS drop a trailing space or dot from a
/// file name, so a name ending in either is trimmed.
/// </summary>
internal static class EntryNames
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding> CodePage437 = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437);
    });

    /// <summary>
    /// Decodes a stored name as UTF-8 when it is valid UTF-8, otherwise as code page 437. macOS Archive Utility
    /// and several Linux tools write UTF-8 names without bit 11, so plain 437 would break those; genuine
    /// 437 text with high bytes is almost never valid UTF-8. The whole name is decoded at once, as is
    /// every name in a zip.
    /// </summary>
    internal static string Decode(ReadOnlySpan<byte> raw)
    {
        try
        {
            return StrictUtf8.GetString(raw);
        }
        catch (DecoderFallbackException)
        {
            return CodePage437.Value.GetString(raw);
        }
    }

    internal static bool IsValidUtf8(ReadOnlySpan<byte> raw) => System.Text.Unicode.Utf8.IsValid(raw);

    /// <summary>
    /// False for a name that neither a list file nor an argument can give to 7z: one that is not valid UTF-8
    /// and ends in whitespace. 7z trims trailing whitespace from a list line, and an argument has to be
    /// valid UTF-8 to be passed at all.
    /// </summary>
    internal static bool IsAddressable(ReadOnlySpan<byte> raw) => IsValidUtf8(raw) || raw.Length == 0 || !IsAsciiWhitespace(raw[^1]);

    /// <summary>Whether 7z would lose the end of this name in a list file.</summary>
    internal static bool EndsInWhitespace(ReadOnlySpan<byte> raw) => raw.Length > 0 && IsAsciiWhitespace(raw[^1]);

    private static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\v' or (byte)'\f' or (byte)'\r';

    /// <summary>
    /// Makes one path segment usable as a file name: control characters, U+FFFD and lone surrogates become
    /// <c>_</c>, trailing spaces and dots go, and a segment with nothing left becomes <c>_</c>. NUL is left
    /// for <see cref="ArchiveEntryPath.Clean"/>, which drops the segment.
    /// </summary>
    internal static string FixSegment(string segment)
    {
        var replaced = ReplaceUnusableCharacters(segment).TrimEnd(' ', '.');
        return replaced.Length == 0 && segment.Length > 0 ? "_" : replaced;
    }

    /// <summary>Applies <see cref="FixSegment"/> to every segment of a path and drops a trailing separator.</summary>
    internal static string FixPath(string path)
    {
        var result = new StringBuilder(path.Length);
        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i < path.Length && path[i] is not ('/' or '\\')) continue;

            if (i > start) result.Append(FixSegment(path[start..i]));
            if (i < path.Length) result.Append(path[i]);
            start = i + 1;
        }

        return result.ToString().TrimEnd('/', '\\');
    }

    private static string ReplaceUnusableCharacters(string name)
    {
        if (name.Length == 0) return name;
        return string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                var unusable = c != '\0' && (char.IsControl(c) || c == '�' || IsLoneSurrogate(source, i));
                span[i] = unusable ? '_' : c;
            }
        });
    }

    private static bool IsLoneSurrogate(string s, int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c)) return !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]));
        if (char.IsLowSurrogate(c)) return !(i > 0 && char.IsHighSurrogate(s[i - 1]));
        return false;
    }

    /// <summary>A name for a log line: control characters and U+FFFD are spelled out.</summary>
    internal static string Printable(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(char.IsControl(c) || c == '�' ? $"\\u{(int)c:X4}" : c);
        return builder.ToString();
    }
}
