using SilverModManager.Sdk.Hashes;

namespace SilverModManager.Library.GitHub;

/// <summary>
/// The digests a release's checksum file lists, by asset name. A checksum file is one more asset of the release,
/// named like <c>SHA256SUMS</c> or <c>tool.7z.sha256</c>.
/// </summary>
internal sealed class ReleaseSums
{
    private readonly Dictionary<string, Sha256Value> _digests = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every digest the file lists, by asset name without regard to case.</summary>
    public IReadOnlyDictionary<string, Sha256Value> Digests => _digests;

    /// <summary>The digest listed for the asset, or null when the file does not list it.</summary>
    public Sha256Value? For(string assetName) => _digests.TryGetValue(assetName, out var digest) ? digest : null;

    /// <summary>Whether a release asset of this name is a checksum file.</summary>
    public static bool IsSidecarName(string assetName)
        => assetName.Contains("SUMS", StringComparison.OrdinalIgnoreCase)
           || assetName.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase)
           || assetName.EndsWith(".sha256sum", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads a checksum file. Lines in the <c>sha256sum</c> format (<c>hex  name</c>, <c>hex *name</c>) and the BSD
    /// format (<c>SHA256 (name) = hex</c>) are understood. A line holding only a digest belongs to the asset the file is
    /// named after (<c>tool.7z.sha256</c> for <c>tool.7z</c>). Anything else is skipped: the file is not trusted
    /// to be well formed, and a line that does not parse must not stop the ones that do.
    /// </summary>
    public static ReleaseSums Parse(string sidecarName, string text)
    {
        var sums = new ReleaseSums();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (TryParseBsd(line, out var bsdName, out var bsdDigest))
            {
                sums._digests[bsdName] = bsdDigest;
                continue;
            }

            var space = line.IndexOfAny([' ', '\t']);
            var hex = space < 0 ? line : line[..space];
            if (!IsDigest(hex, out var digest)) continue;

            var name = space < 0 ? NameBefore(sidecarName) : line[(space + 1)..].Trim().TrimStart('*');
            if (name.Length > 0) sums._digests[name] = digest;
        }

        return sums;
    }

    private static bool TryParseBsd(string line, out string name, out Sha256Value digest)
    {
        name = "";
        digest = default;
        const string prefix = "SHA256 (";
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var close = line.LastIndexOf(") = ", StringComparison.Ordinal);
        if (close < prefix.Length) return false;
        if (!IsDigest(line[(close + 4)..].Trim(), out digest)) return false;

        name = line[prefix.Length..close];
        return name.Length > 0;
    }

    private static string NameBefore(string sidecarName)
    {
        foreach (var suffix in (ReadOnlySpan<string>)[".sha256sum", ".sha256"])
        {
            if (sidecarName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return sidecarName[..^suffix.Length];
        }

        return "";
    }

    private static bool IsDigest(string text, out Sha256Value digest)
    {
        digest = default;
        return text.Length == 64 && text.All(char.IsAsciiHexDigit) && Sha256Value.TryParse(text.AsSpan(), out digest);
    }
}
