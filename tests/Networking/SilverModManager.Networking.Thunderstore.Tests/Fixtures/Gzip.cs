using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace NexusMods.Networking.Thunderstore.Tests.Fixtures;

internal static class Gzip
{
    public static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return output.ToArray();
    }

    public static string HexDigest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
