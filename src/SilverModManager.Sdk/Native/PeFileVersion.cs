using System.Buffers.Binary;

namespace SilverModManager.Sdk.Native;

/// <summary>
/// Reads the numeric file version (<c>FILEVERSION</c>) from a PE file's <c>VS_FIXEDFILEINFO</c>. A scan for the
/// struct's signature, not a walk of the resource directory: the struct has a fixed layout and an executable carries
/// one. The reader in <c>NexusMods.Paths</c> takes its version from the string table instead, which is not the
/// number a game's own updater compares.
/// </summary>
public static class PeFileVersion
{
    private const uint Signature = 0xFEEF04BD;

    /// <summary>The only value <c>dwStrucVersion</c> has ever carried; it tells the struct from four coincidental bytes in a large binary.</summary>
    private const uint StructVersion = 0x00010000;

    /// <summary>Signature, struct version, then the high and low words of the file version.</summary>
    private const int FieldsLength = 16;

    private const int BlockSize = 64 * 1024;

    /// <summary>
    /// The file version as <c>major.minor.build.revision</c>, or null when the stream holds no version struct.
    /// </summary>
    public static string? Read(Stream stream)
    {
        Span<byte> signature = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(signature, Signature);

        // Each block is searched together with the unread tail of the one before, so a struct that straddles
        // two reads is still found.
        var buffer = new byte[BlockSize + FieldsLength];
        var kept = 0;
        while (true)
        {
            var read = stream.Read(buffer, kept, BlockSize);
            var end = kept + read;

            var from = 0;
            while (from < end)
            {
                var at = buffer.AsSpan(from, end - from).IndexOf(signature);
                if (at < 0) break;
                at += from;

                if (at + FieldsLength > end)
                {
                    // Cut off by the end of the block: carry it over and finish it with the next read.
                    if (read == 0) return null;
                    kept = end - at;
                    Buffer.BlockCopy(buffer, at, buffer, 0, kept);
                    goto nextBlock;
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 4)) == StructVersion)
                {
                    var ms = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 8));
                    var ls = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 12));
                    return $"{ms >> 16}.{ms & 0xFFFF}.{ls >> 16}.{ls & 0xFFFF}";
                }

                from = at + 1;
            }

            if (read == 0) return null;
            kept = Math.Min(end, FieldsLength - 1);
            Buffer.BlockCopy(buffer, end - kept, buffer, 0, kept);

            nextBlock: ;
        }
    }
}
