using System.Buffers.Binary;
using System.Text;

namespace SilverModManager.Games.CreationEngine.Creations;

/// <summary>
/// Reads a <c>Creations/&lt;id&gt;-&lt;n&gt;.manifest</c> file: back-to-back entries of a little-endian
/// uint16 length and that many bytes of <c>data/&lt;file&gt;</c>.
/// </summary>
public static class CreationsManifestReader
{
    private const string DataPrefix = "data/";

    /// <summary>The files the manifest lists, relative to the Data folder.</summary>
    /// <exception cref="CreationsRecordException">The stream ends inside an entry.</exception>
    public static IReadOnlyList<string> Read(Stream stream)
    {
        var files = new List<string>();
        Span<byte> length = stackalloc byte[2];
        while (true)
        {
            var got = stream.ReadAtLeast(length, 2, throwOnEndOfStream: false);
            if (got == 0) return files;
            if (got < 2) throw new CreationsRecordException("The manifest ends inside an entry length");

            var bytes = new byte[BinaryPrimitives.ReadUInt16LittleEndian(length)];
            if (stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length)
                throw new CreationsRecordException("The manifest ends inside an entry");

            var entry = Encoding.UTF8.GetString(bytes);
            files.Add(entry.StartsWith(DataPrefix, StringComparison.OrdinalIgnoreCase) ? entry[DataPrefix.Length..] : entry);
        }
    }

    /// <summary>
    /// The Creation's id: the file name before the first dash, or the name without its extension when it has none.
    /// </summary>
    public static string IdOf(string manifestFileName)
    {
        var dash = manifestFileName.IndexOf('-');
        return dash >= 0 ? manifestFileName[..dash] : Path.GetFileNameWithoutExtension(manifestFileName);
    }
}
