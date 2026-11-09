using System.Buffers.Binary;

namespace SilverModManager.Sdk.Native;

/// <summary>
/// Reads the numeric file version (<c>FILEVERSION</c>) from a PE file's <c>VS_FIXEDFILEINFO</c>. A scan for the
/// struct's signature, not a walk of the resource directory: the struct has a fixed layout and an executable carries
/// one. The reader in <c>NexusMods.Paths</c> takes its version from the string table instead, and finds nothing in a
/// native executable on Linux; <see cref="ReadProductVersion"/> reads that table directly. Neither source is always
/// right: SkyrimVR.exe has a 1.0.0.0 placeholder in this numeric block and the real version only in the string table,
/// so <c>GameExecutableVersion</c> tries the string table first and falls back to this block.
/// </summary>
public static class PeFileVersion
{
    private const uint Signature = 0xFEEF04BD;

    /// <summary>The only value <c>dwStrucVersion</c> has ever carried; it tells the struct from four coincidental bytes in a large binary.</summary>
    private const uint StructVersion = 0x00010000;

    /// <summary>Signature, struct version, then the high and low words of the file version.</summary>
    private const int FieldsLength = 16;

    private const int BlockSize = 64 * 1024;

    /// <summary>The fixed info sits this far into <c>VS_VERSIONINFO</c>: wLength, wValueLength, wType, the 32-byte key "VS_VERSION_INFO" and 2 bytes of padding.</summary>
    private const int VersionInfoHeaderLength = 40;

    private const int FixedFileInfoLength = 0x34;

    /// <summary>wLength, wValueLength and wType, which every node of a version resource starts with.</summary>
    private const int NodeHeaderLength = 6;

    private static ReadOnlySpan<byte> VersionInfoKey => "V\0S\0_\0V\0E\0R\0S\0I\0O\0N\0_\0I\0N\0F\0O\0\0\0"u8;

    /// <summary>
    /// The file version as <c>major.minor.build.revision</c>, or null when the stream holds no version struct.
    /// </summary>
    public static string? Read(Stream stream) => ReadVersion(stream)?.ToString();

    /// <summary>
    /// The same version as <see cref="Read"/>, as a <see cref="Version"/>. The words come out as
    /// major.minor.build.revision: the high and low halves of <c>dwFileVersionMS</c>, then of
    /// <c>dwFileVersionLS</c>.
    /// </summary>
    public static Version? ReadVersion(Stream stream)
    {
        if (FindFixedFileInfo(stream, out var ms, out var ls) < 0) return null;

        return new Version((int)(ms >> 16), (int)(ms & 0xFFFF), (int)(ls >> 16), (int)(ls & 0xFFFF));
    }

    /// <summary>
    /// The <c>ProductVersion</c> string of the first string table, as written (for example <c>1, 6, 1170, 0</c>),
    /// or null when the file has no version resource or no such string. Starts from the beginning of
    /// <paramref name="stream"/>, which has to be seekable.
    /// </summary>
    public static string? ReadProductVersion(Stream stream)
    {
        stream.Position = 0;
        var fixedInfo = FindFixedFileInfo(stream, out _, out _);
        if (fixedInfo < VersionInfoHeaderLength) return null;

        var start = fixedInfo - VersionInfoHeaderLength;
        stream.Position = start;
        Span<byte> header = stackalloc byte[VersionInfoHeaderLength];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length) return null;

        var length = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        if (valueLength != FixedFileInfoLength || length < VersionInfoHeaderLength + FixedFileInfoLength) return null;
        if (!header[6..].StartsWith(VersionInfoKey)) return null;

        var resource = new byte[length];
        header.CopyTo(resource);
        if (stream.ReadAtLeast(resource.AsSpan(header.Length), length - header.Length, throwOnEndOfStream: false) < length - header.Length)
            return null;

        return FindString(resource.AsSpan(VersionInfoHeaderLength + FixedFileInfoLength), Level.VersionInfo);
    }

    /// <summary>
    /// Whether the stream starts like a PE image: <c>MZ</c>, a <c>PE\0\0</c> signature where <c>e_lfanew</c> points,
    /// and a PE32 or PE32+ optional header after the COFF header.
    /// </summary>
    public static bool IsPortableExecutable(Stream stream)
    {
        Span<byte> dos = stackalloc byte[0x40];
        stream.Position = 0;
        if (stream.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z') return false;

        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dos[0x3C..]);
        if (peOffset > stream.Length) return false;

        // Signature (4), COFF header (20), then the optional header, whose first word says PE32 or PE32+.
        Span<byte> pe = stackalloc byte[26];
        stream.Position = peOffset;
        if (stream.ReadAtLeast(pe, pe.Length, throwOnEndOfStream: false) < pe.Length || !pe.StartsWith("PE\0\0"u8)) return false;

        return BinaryPrimitives.ReadUInt16LittleEndian(pe[24..]) is 0x10b or 0x20b;
    }

    private enum Level { VersionInfo, StringFileInfo, StringTable }

    /// <summary>
    /// Walks the sibling nodes in <paramref name="nodes"/>: at the top <c>StringFileInfo</c>, inside it each
    /// <c>StringTable</c>, inside that each <c>String</c>, looking for <c>ProductVersion</c>. A node whose
    /// length runs past the buffer ends the walk.
    /// </summary>
    private static string? FindString(ReadOnlySpan<byte> nodes, Level level)
    {
        var at = 0;
        while (at + NodeHeaderLength <= nodes.Length)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(nodes[at..]);
            var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(nodes[(at + 2)..]);
            if (length < NodeHeaderLength || at + length > nodes.Length) return null;

            var node = nodes.Slice(at, length);
            var keyLength = KeyLength(node[NodeHeaderLength..]);
            if (keyLength < 0) return null;

            var key = System.Text.Encoding.Unicode.GetString(node.Slice(NodeHeaderLength, keyLength));
            var body = Align4(NodeHeaderLength + keyLength + 2);

            switch (level)
            {
                case Level.VersionInfo when key == "StringFileInfo" && body <= length:
                    if (FindString(node[body..], Level.StringFileInfo) is { } fromInfo) return fromInfo;
                    break;
                case Level.StringFileInfo when body <= length:
                    if (FindString(node[body..], Level.StringTable) is { } fromTable) return fromTable;
                    break;
                case Level.StringTable when key == "ProductVersion":
                    var valueBytes = valueLength * 2;
                    if (body + valueBytes > length) return null;
                    return System.Text.Encoding.Unicode.GetString(node.Slice(body, valueBytes)).TrimEnd('\0');
            }

            at += Align4(length);
        }

        return null;
    }

    /// <summary>The byte length of the null-terminated UTF-16 key at the start of <paramref name="bytes"/>, without the terminator, or -1.</summary>
    private static int KeyLength(ReadOnlySpan<byte> bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i += 2)
        {
            if (bytes[i] == 0 && bytes[i + 1] == 0) return i;
        }

        return -1;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    /// <summary>
    /// Finds the <c>VS_FIXEDFILEINFO</c> struct and returns its offset from the start of the stream, or -1. The
    /// stream is read from its current position, and that position is taken to be the offset of the first byte.
    /// </summary>
    private static long FindFixedFileInfo(Stream stream, out uint fileVersionMs, out uint fileVersionLs)
    {
        fileVersionMs = fileVersionLs = 0;
        Span<byte> signature = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(signature, Signature);

        // Each block is searched together with the unread tail of the one before, so a struct that straddles
        // two reads is still found. bufferStart is the stream offset of buffer[0].
        var buffer = new byte[BlockSize + FieldsLength];
        var bufferStart = stream.Position;
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
                    if (read == 0) return -1;
                    kept = end - at;
                    Buffer.BlockCopy(buffer, at, buffer, 0, kept);
                    bufferStart += at;
                    goto nextBlock;
                }

                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 4)) == StructVersion)
                {
                    fileVersionMs = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 8));
                    fileVersionLs = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at + 12));
                    return bufferStart + at;
                }

                from = at + 1;
            }

            if (read == 0) return -1;
            kept = Math.Min(end, FieldsLength - 1);
            Buffer.BlockCopy(buffer, end - kept, buffer, 0, kept);
            bufferStart += end - kept;

            nextBlock: ;
        }
    }
}
