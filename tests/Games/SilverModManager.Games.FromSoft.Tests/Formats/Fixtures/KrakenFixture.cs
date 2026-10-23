namespace NexusMods.Games.FromSoft.Tests.Formats.Fixtures;

/// <summary>Builds Kraken streams the decoder accepts without needing an encoder.</summary>
public static class KrakenFixture
{
    /// <summary>The most payload one chunk carries (<c>Kraken_DecodeStep</c> takes <c>Min(0x40000, bytes left)</c>).</summary>
    public const int ChunkSize = 0x40000;

    /// <summary>
    /// A stored (uncompressed) Kraken stream. Each chunk is two header bytes followed by the raw payload.
    /// The decode entry point passes offset 0 on every step, so every chunk carries its own header, not just the first.
    /// <list type="bullet">
    /// <item><c>0x4C</c> (<c>Kraken_ParseHeader</c>): the low nibble must be <c>0xC</c>, bits 4-5 must be zero,
    /// bit 6 means "uncompressed" and bit 7 "restart decoder", which is left clear.</item>
    /// <item><c>0x06</c>: bits 0-6 are the decoder type (6 is Kraken), bit 7 would add checksums.</item>
    /// </list>
    /// </summary>
    public static byte[] Stored(ReadOnlySpan<byte> payload)
    {
        var chunks = Math.Max(1, (payload.Length + ChunkSize - 1) / ChunkSize);
        var stream = new byte[payload.Length + chunks * 2];
        var written = 0;
        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var offset = chunk * ChunkSize;
            var size = Math.Min(ChunkSize, payload.Length - offset);
            stream[written++] = 0x4C;
            stream[written++] = 0x06;
            payload.Slice(offset, size).CopyTo(stream.AsSpan(written));
            written += size;
        }
        return stream;
    }

    /// <summary>A payload whose bytes differ across the 256 KiB chunk boundary, so a chunk decoded from the wrong place is caught.</summary>
    public static byte[] Payload(int length)
    {
        var payload = new byte[length];
        new Random(1234).NextBytes(payload);
        return payload;
    }
}
