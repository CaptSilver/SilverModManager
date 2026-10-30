using System.Diagnostics;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;
using SilverModManager.Abstractions.FileExtractor;
using SilverModManager.FileExtractor;
using NexusMods.Paths;
using SilverModManager.Sdk.IO;

namespace SilverModManager.Backend.FileExtractor.Extractors;

internal class ManagedZipExtractor : IExtractor
{
    public FileType[] SupportedSignatures { get; } = [FileType.ZIP];
    public Extension[] SupportedExtensions { get; } = [new(".zip")];

    private readonly ILogger _logger;

    public ManagedZipExtractor(ILogger<ManagedZipExtractor> logger)
    {
        _logger = logger;
    }

    // The zip spec stores a name as code page 437 unless general purpose bit 11 says UTF-8. SharpZipLib's
    // default decodes an unflagged name with the host's encoding, which on Linux is UTF-8, so a CP437 name
    // came out as U+FFFD and the path layer rejected the entry. Plain CP437 would break the other way: macOS
    // and several Linux tools write UTF-8 names without the flag, and those must keep decoding as UTF-8.
    private static readonly Encoding _codePage437 = GetCodePage437();
    private static readonly StringCodec _stringCodec = StringCodec.FromEncoding(new Utf8ThenCodePage437Encoding(_codePage437));

    private static Encoding GetCodePage437()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(437);
    }

    /// <summary>
    /// Decodes bytes as UTF-8 when they are valid UTF-8, otherwise as code page 437. Genuine CP437 text with
    /// high bytes is almost never valid UTF-8, and every name that decoded before still decodes the same way.
    /// </summary>
    private sealed class Utf8ThenCodePage437Encoding(Encoding codePage437) : Encoding
    {
        private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private bool TryUtf8(ReadOnlySpan<byte> bytes, out string text)
        {
            try
            {
                text = _strictUtf8.GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                text = string.Empty;
                return false;
            }
        }

        private string Decode(ReadOnlySpan<byte> bytes) => TryUtf8(bytes, out var text) ? text : codePage437.GetString(bytes);

        public override int GetByteCount(char[] chars, int index, int count) => codePage437.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => codePage437.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => Decode(bytes.AsSpan(index, count)).Length;

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            var text = Decode(bytes.AsSpan(byteIndex, byteCount));
            text.CopyTo(0, chars, charIndex, text.Length);
            return text.Length;
        }

        public override string GetString(byte[] bytes, int index, int count) => Decode(bytes.AsSpan(index, count));
        public override int GetMaxByteCount(int charCount) => codePage437.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => byteCount;
    }

    public async Task ExtractAllAsync(IStreamFactory source, AbsolutePath destination, CancellationToken cancellationToken = default)
    {
        await using var stream = await source.GetStreamAsync().ConfigureAwait(false);

        using var zipFile = new ZipFile(stream, leaveOpen: false, stringCodec: _stringCodec);
        var rewrites = new ArchiveEntryPath.Rewrites();
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        var replacedNames = new List<string>();
        for (var i = 0; i < zipFile.Count; i++)
        {
            var zipEntry = zipFile[i];

            var originalName = ReplaceUnusableCharacters(GetName(zipEntry), replacedNames);
            var cleaned = ArchiveEntryPath.Clean(originalName);
            if (ArchiveEntryPath.IsRewritten(originalName, cleaned)) rewrites.Add(originalName, cleaned);
            if (cleaned.Length == 0) continue;

            // The later entry wins, as it does for two entries with the same plain name. Different raw names
            // that clean to one path are reported, since the author probably didn't mean to overwrite.
            if (!zipEntry.IsDirectory)
            {
                if (written.TryGetValue(cleaned, out var earlier) && earlier != originalName) rewrites.AddOverwritten(earlier, cleaned);
                written[cleaned] = originalName;
            }

            var fixedPath = PathsHelper.FixPath(cleaned);
            var relativePath = RelativePath.FromUnsanitizedInput(fixedPath);
            var absolutePath = destination.Combine(relativePath);

            if (zipEntry.IsDirectory)
            {
                if (!absolutePath.DirectoryExists()) absolutePath.CreateDirectory();
                continue;
            }

            var parent = absolutePath.Parent;
            if (!parent.DirectoryExists()) parent.CreateDirectory();

            await using var outputStream = absolutePath.Open(mode: FileMode.Create, access: FileAccess.ReadWrite, share: FileShare.Read);
            outputStream.SetLength(zipEntry.Size);

            await using var inputStream = zipFile.GetInputStream(zipEntry);
            await inputStream.CopyToAsync(outputStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        rewrites.Log(_logger, source.FileName.ToString());
        if (replacedNames.Count > 0)
        {
            _logger.LogWarning("Replaced control or undecodable characters with `_` in {Count} entry name(s) of archive `{Archive}`: {Entries}",
                replacedNames.Count, source.FileName.ToString(), string.Join("; ", replacedNames));
        }
    }

    /// <summary>
    /// Swaps characters that can't be part of a file name (control characters, U+FFFD, lone surrogates) for
    /// <c>_</c>, so a name that can't be decoded cleanly still extracts under the same name every time.
    /// NUL is left for <see cref="ArchiveEntryPath.Clean"/>, which drops the segment.
    /// </summary>
    private static string ReplaceUnusableCharacters(string name, List<string> replaced)
    {
        var result = name.Length == 0 ? name : string.Create(name.Length, name, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                var unusable = c != '\0' && (char.IsControl(c) || c == '\uFFFD' || IsLoneSurrogate(source, i));
                span[i] = unusable ? '_' : c;
            }
        });

        if (!string.Equals(result, name, StringComparison.Ordinal)) replaced.Add($"`{Printable(name)}` -> `{result}`");
        return result;
    }

    private static bool IsLoneSurrogate(string s, int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c)) return !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]));
        if (char.IsLowSurrogate(c)) return !(i > 0 && char.IsHighSurrogate(s[i - 1]));
        return false;
    }

    private static string Printable(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name) builder.Append(char.IsControl(c) || c == '\uFFFD' ? $"\\u{(int)c:X4}" : c);
        return builder.ToString();
    }

    private string GetName(ZipEntry zipEntry)
    {
        // NOTE(erri120): A ZIP entry name by default is encoded using CP436. If the general purpose bit flag 11
        // is set, then the name is instead encoded using UTF8. The SharpZipLib library handles that for us, and `zipEntry.Name`
        // will be a proper UTF8 decoded string if the flag was set.
        // However, the SharpZipLib library doesn't handle the 0x7076 extra data field which can contain the
        // UTF8 encoded path.

        var originalName = zipEntry.Name;
        if (zipEntry.HasFlag(GeneralBitFlags.UnicodeText)) return originalName;
        if (!TryGetUnicodeName(originalName, zipEntry.ExtraData, out var unicodeName, out var hashesMatch)) return originalName;

        if (!hashesMatch)
        {
            // NOTE(erri120): The CRC hash in the extra data field doesn't match with the computed hash of the original name
            // if the original name was encoded using an encoding different from the default encoding. It's not an issue,
            // it just means that now know that the original name was decoded with the wrong encoding.
            _logger.LogWarning("CRC hashes didn't match for encoded name in ZIP entry: Original name={OriginalName}, Unicode name={UnicodeName}", originalName, unicodeName);
        }

        return unicodeName;
    }

    private const ushort UnicodePathHeaderId = 0x7075;
    private static bool TryGetUnicodeName(ReadOnlySpan<char> originalName, ReadOnlySpan<byte> extraData, out string unicodeName, out bool hashesMatch)
    {
        unicodeName = string.Empty;
        hashesMatch = false;
        if (extraData.Length < ExtraDataHeader.Size) return false;

        // Layout:
        // |-- Header 1..N
        // |-- ushort HeaderId
        // |-- ushort DataLength
        // |-- byte[DataLength] Data
        // continue until everything is read

        var index = 0;
        while (index + ExtraDataHeader.Size < extraData.Length)
        {
            var span = extraData.Slice(start: index, length: ExtraDataHeader.Size);
            var header = MemoryMarshal.Read<ExtraDataHeader>(span);

            if (header.HeaderId != UnicodePathHeaderId)
            {
                index += ExtraDataHeader.Size + header.DataSize;
                continue;
            }

            var dataSpan = extraData.Slice(start: index + ExtraDataHeader.Size, length: header.DataSize);
            (unicodeName, hashesMatch) = DecodeUnicodeName(originalName, dataSpan);
            return true;
        }

        return false;
    }

    private static (string UnicodeName, bool HashesMatch) DecodeUnicodeName(ReadOnlySpan<char> originalName, ReadOnlySpan<byte> span)
    {
        // Layout
        // byte version (should always be 1)
        // uint crcHash (hash of the original name)
        // byte[] utf8Name (remaining data is the UTF8 encoded name)

        var index = 0;

        var version = span[index++];
        Debug.Assert(version == 1, $"unknown version `{version}`");

        var actualHash = BitConverter.ToUInt32(span.Slice(start: index, length: sizeof(uint)));
        index += sizeof(uint);

        var expectedHash = GetCrcHash(originalName);
        var hashesMatch = actualHash == expectedHash;

        var nameSlice = span.Slice(start: index);
        var unicodeName = Encoding.UTF8.GetString(nameSlice);
        return (unicodeName, hashesMatch);
    }

    private static uint GetCrcHash(ReadOnlySpan<char> originalName)
    {
        var byteCount = _codePage437.GetByteCount(originalName);
        var bytes = byteCount < 1024 ? stackalloc byte[byteCount] : GC.AllocateUninitializedArray<byte>(byteCount);
        var actualCount = _codePage437.GetBytes(originalName, bytes);
        Debug.Assert(actualCount == byteCount);

        Span<byte> hashBytes = stackalloc byte[sizeof(uint)];
        var hashBytesCount = Crc32.Hash(bytes.Slice(start: 0, length: actualCount), hashBytes);
        Debug.Assert(hashBytesCount == sizeof(uint));

        return BitConverter.ToUInt32(hashBytes);
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct ExtraDataHeader
    {
        public const int Size = 4;

        [FieldOffset(0)] public readonly ushort HeaderId;
        [FieldOffset(2)] public readonly ushort DataSize;

        static ExtraDataHeader()
        {
            unsafe
            {
                var size = sizeof(ExtraDataHeader);
                if (size != Size) throw new Exception($"Expected size of be {Size} but found {size} for {typeof(ExtraDataHeader)}");
            }
        }
    }

    public Priority DeterminePriority(IEnumerable<FileType> signatures)
    {
        foreach (var signature in signatures)
        {
            if (signature == FileType.ZIP) return Priority.Highest;
        }

        return Priority.None;
    }
}
