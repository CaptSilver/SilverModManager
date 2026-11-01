using System.Text;

namespace SilverModManager.App.UI.Windows.Xdnd;

/// <summary>text/uri-list (RFC 2483) to local paths. Works on bytes: Linux paths are bytes and a non-UTF-8 name is a refusal, not a mojibake path.</summary>
public sealed class UriListParser
{
    private static readonly byte[] FileScheme = "file:"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    public UriListParse Parse(ReadOnlySpan<byte> bytes)
    {
        var paths = new List<string>();
        var skipped = new List<UriListSkip>();

        while (!bytes.IsEmpty)
        {
            var end = bytes.IndexOf((byte)'\n');
            var line = end < 0 ? bytes : bytes[..end];
            bytes = end < 0 ? [] : bytes[(end + 1)..];

            line = DropLineEnding(line);
            if (IsBlank(line) || line[0] == (byte)'#') continue;

            var rejection = TryReadPath(line, out var path);
            if (rejection is null) paths.Add(path);
            else skipped.Add(new UriListSkip(Encoding.UTF8.GetString(line), rejection));
        }

        return new UriListParse(paths, skipped);
    }

    /// <summary>The reason the line is refused, or null with the decoded path.</summary>
    private static string? TryReadPath(ReadOnlySpan<byte> line, out string path)
    {
        path = "";
        if (!StartsWithScheme(line)) return "not a local file";

        var rest = line[FileScheme.Length..];
        if (rest.StartsWith("//"u8))
        {
            rest = rest[2..];
            var slash = rest.IndexOf((byte)'/');
            if (slash < 0) return "not a local file";
            if (!IsThisMachine(rest[..slash])) return "on another machine";
            rest = rest[slash..];
        }
        if (rest.IsEmpty || rest[0] != (byte)'/') return "not a local file";

        var decoded = PercentDecode(rest);
        if (decoded.Contains((byte)0)) return "name contains a NUL byte";
        try
        {
            path = Path.TrimEndingDirectorySeparator(StrictUtf8.GetString(decoded));
        }
        catch (DecoderFallbackException)
        {
            return "name is not valid UTF-8";
        }
        return null;
    }

    private static bool StartsWithScheme(ReadOnlySpan<byte> line) =>
        line.Length >= FileScheme.Length && Ascii.EqualsIgnoreCase(line[..FileScheme.Length], FileScheme);

    private static bool IsThisMachine(ReadOnlySpan<byte> authority) =>
        authority.IsEmpty || Ascii.EqualsIgnoreCase(authority, "localhost"u8);

    private static byte[] PercentDecode(ReadOnlySpan<byte> encoded)
    {
        var output = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == (byte)'%' && i + 2 < encoded.Length && TryHex(encoded[i + 1], encoded[i + 2], out var value))
            {
                output.Add(value);
                i += 2;
            }
            else output.Add(encoded[i]);
        }
        return [.. output];
    }

    private static bool TryHex(byte high, byte low, out byte value)
    {
        var h = HexValue(high);
        var l = HexValue(low);
        value = (byte)(h << 4 | l);
        return h >= 0 && l >= 0;
    }

    private static int HexValue(byte c) => c switch
    {
        >= (byte)'0' and <= (byte)'9' => c - '0',
        >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Removes the CR of a CRLF and nothing else: raw spaces and tabs at either end can be part of an unescaped name.</summary>
    private static ReadOnlySpan<byte> DropLineEnding(ReadOnlySpan<byte> line) =>
        !line.IsEmpty && line[^1] == (byte)'\r' ? line[..^1] : line;

    private static bool IsBlank(ReadOnlySpan<byte> line)
    {
        foreach (var b in line)
        {
            if (b is not ((byte)' ' or (byte)'\t')) return false;
        }
        return true;
    }
}

public sealed record UriListParse(IReadOnlyList<string> LocalPaths, IReadOnlyList<UriListSkip> Skipped);

public sealed record UriListSkip(string Uri, string Reason);
