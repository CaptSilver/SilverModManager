namespace SilverModManager.Wabbajack.Acquire.Hosts;

/// <summary>
/// A MEGA public file link: the id the API knows the file by and the 256-bit key that encrypts it. The key never goes
/// to MEGA; it only decrypts what comes back. Both link forms carry the same two parts: <c>/file/&lt;id&gt;#&lt;key&gt;</c>
/// and the older <c>/#!&lt;id&gt;!&lt;key&gt;</c>.
/// </summary>
public sealed class MegaLink
{
    private MegaLink(string fileId, byte[] key)
    {
        FileId = fileId;
        Key = key;
    }

    public string FileId { get; }

    /// <summary>The 32 key bytes as the link gives them.</summary>
    public byte[] Key { get; }

    /// <summary>
    /// The AES-128 key: the key read as eight big-endian words, the first four xored with the last four. Byte-wise
    /// xor gives the same bytes, since xor does not mix neighbouring bytes.
    /// </summary>
    public byte[] AesKey
    {
        get
        {
            var key = new byte[16];
            for (var i = 0; i < 16; i++) key[i] = (byte)(Key[i] ^ Key[i + 16]);
            return key;
        }
    }

    /// <summary>The first half of the CTR counter block: words four and five of the key. The counter half starts at zero.</summary>
    public byte[] Nonce => Key[16..24];

    public static bool TryParse(Uri url, out MegaLink? link)
    {
        link = null;
        var host = url.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? url.Host[4..] : url.Host;
        if (!host.Equals("mega.nz", StringComparison.OrdinalIgnoreCase) && !host.Equals("mega.co.nz", StringComparison.OrdinalIgnoreCase)) return false;

        string id;
        string keyText;
        var fragment = url.Fragment.TrimStart('#');
        if (fragment.StartsWith('!'))
        {
            var parts = fragment[1..].Split('!');
            if (parts.Length < 2) return false;
            (id, keyText) = (parts[0], parts[1]);
        }
        else
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 2 || !segments[0].Equals("file", StringComparison.OrdinalIgnoreCase)) return false;
            (id, keyText) = (segments[1], fragment);
        }

        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        if (!TryDecode(keyText, out var key) || key.Length != 32) return false;

        link = new MegaLink(id, key);
        return true;
    }

    /// <summary>MEGA's base64: URL-safe alphabet, no padding.</summary>
    internal static bool TryDecode(string text, out byte[] bytes)
    {
        bytes = [];
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return false;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight((padded.Length + 3) / 4 * 4, '=');
        var buffer = new byte[padded.Length];
        if (!Convert.TryFromBase64String(padded, buffer, out var written)) return false;
        bytes = buffer[..written];
        return true;
    }
}
