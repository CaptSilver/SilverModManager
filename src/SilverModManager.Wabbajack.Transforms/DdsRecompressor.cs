// Follows Wabbajack.Hashing.PHash/CrossPlatformImageLoader.cs, with SkiaSharp doing the resize.
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Re-encodes a DDS texture to the size, mip count and format a modlist recorded for it.
/// </summary>
public sealed class DdsRecompressor
{
    private readonly ITextureEncoder[] _encoders;

    /// <summary>The encoders in order of preference: the first that can encode a <see cref="BlockFormat"/> is used for it.</summary>
    public DdsRecompressor(IEnumerable<ITextureEncoder> encoders)
    {
        _encoders = [.. encoders];
        if (_encoders.Length == 0) throw new ArgumentException("A recompressor needs at least one encoder", nameof(encoders));
    }

    /// <summary>A recompressor over BCnEncoder alone.</summary>
    public static DdsRecompressor Managed() => new([new ManagedTextureEncoder()]);

    /// <summary>
    /// Decodes the top mip of every face of <paramref name="dds"/>, resizes it to the target and writes
    /// a new DDS with the target format and mip chain to <paramref name="output"/>. Neither stream is closed.
    /// The CPU-bound decode and encode run on a thread-pool thread. <paramref name="parallelism"/> is how many threads
    /// one level may be encoded on: 1 when the caller already runs one texture per worker.
    /// </summary>
    /// <exception cref="UnsupportedTextureFormatException">The source or target format, or the face count, cannot be handled,
    /// or the source is truncated or corrupt (<see cref="UnreadableTextureException"/>). Nothing has been written to <paramref name="output"/> when it is thrown.</exception>
    public Task Recompress(Stream dds, DdsTarget target, Stream output, int parallelism, CancellationToken token)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        token.ThrowIfCancellationRequested();
        return Task.Run(() => RecompressCore(dds, target, output, parallelism, token), token);
    }

    private readonly record struct BlockEncoding(BlockFormat Format, ITextureEncoder Encoder);

    private ITextureEncoder EncoderFor(BlockFormat format, DxgiFormat target)
        => _encoders.FirstOrDefault(e => e.CanEncode(format))
           ?? throw new UnsupportedTextureFormatException($"cannot write {target}: no encoder takes {format}");

    private void RecompressCore(Stream input, DdsTarget target, Stream output, int parallelism, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        input.CopyTo(buffer);
        var dds = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);

        DdsHeader header;
        try
        {
            header = DdsHeader.Read(dds.Span);
        }
        catch (InvalidDataException malformed)
        {
            throw new UnreadableTextureException(malformed.Message, malformed);
        }

        if (target.Format == DxgiFormat.UNKNOWN) target = target with { Format = KeepSourceFormat(header) };

        // Check the target before the expensive decode.
        var packed = UncompressedWriter.Handles(target.Format);
        // An uncompressed target has no block format and never reaches an encoder.
        BlockEncoding? block = null;
        if (!packed)
        {
            var format = ToBlockFormat(target.Format);
            block = new BlockEncoding(format, EncoderFor(format, target.Format));
        }

        var decodeFormat = header.IsBlockCompressed && header.Format is not (DxgiFormat.BC4_SNORM or DxgiFormat.BC5_SNORM)
            ? ToDecodeFormat(header)
            : CompressionFormat.Unknown;

        var decoder = new BcDecoder();
        var faces = new List<ColorRgba32[]>(header.FaceCount);
        for (var face = 0; face < header.FaceCount; face++)
        {
            token.ThrowIfCancellationRequested();
            var data = dds.Slice((int)header.TopLevelOffset(face), (int)header.LevelSize(0));
            var pixels = DecodeFace(header, decoder, decodeFormat, data);
            faces.Add(Resize(pixels, header.Width, header.Height, target.Width, target.Height, header.IsSrgb));
        }

        // BC1 stores 1-bit alpha in its 3-colour blocks. Opaque BC1 would drop a cut-out (foliage), so any pixel under 128
        // anywhere on the top level makes the whole texture a punch-through one. Every level of every face then shares one
        // block format, and the header is the same DXT1 either way.
        if (block is { Format: BlockFormat.Bc1 } && faces.Any(HasCutOut))
            block = new BlockEncoding(BlockFormat.Bc1PunchThrough, EncoderFor(BlockFormat.Bc1PunchThrough, target.Format));

        // MipChain builds the levels from the previous one. A block target filters in the source's colour space and
        // takes the sRGB transfer per level afterwards, as texconv does when it compresses. An uncompressed target
        // takes the transfer first and filters in its own space, as texconv does when it converts. Each level is then
        // packed by UncompressedWriter, or padded to whole blocks and handed to the block encoder, which makes no mip
        // levels of its own.
        var transfer = header.IsSrgb == target.Format.IsSrgb() ? null : target.Format.IsSrgb() ? SrgbTransfer.ToSrgb : SrgbTransfer.ToLinear;
        var filterInLinear = packed ? target.Format.IsSrgb() : header.IsSrgb;
        var levels = MipChain.FullLength(target.Width, target.Height);
        if (target.MipLevels != 0) levels = Math.Min(levels, target.MipLevels);

        // Every level of every face is encoded before the first byte goes to the output, so a failure here
        // leaves the stream untouched.
        var encoded = new List<byte[][]>(faces.Count);
        foreach (var face in faces)
        {
            var top = Bytes(face).ToArray();
            if (packed && transfer is not null) SrgbTransfer.Apply(top, transfer);

            var faceLevels = new byte[levels][];
            var index = 0;
            foreach (var level in MipChain.Build(new RgbaLevel(target.Width, target.Height, top), filterInLinear, levels))
            {
                token.ThrowIfCancellationRequested();
                if (block is not { } blockEncoding)
                {
                    faceLevels[index++] = UncompressedWriter.Pack(level.Pixels, target.Format);
                    continue;
                }

                // The filter's output stays untouched: the next level is built from it, not from the converted copy.
                var pixels = level.Pixels;
                if (transfer is not null)
                {
                    pixels = (byte[])pixels.Clone();
                    SrgbTransfer.Apply(pixels, transfer);
                }
                var padded = MipChain.Pad(level with { Pixels = pixels });
                var blocks = new byte[blockEncoding.Format.BlockBytes(padded.Width, padded.Height)];
                blockEncoding.Encoder.Encode(padded.Pixels, padded.Width, padded.Height, blockEncoding.Format, blocks, parallelism, token);
                faceLevels[index++] = blocks;
            }
            encoded.Add(faceLevels);
        }

        DdsWriter.Write(output, target.Format, target.Width, target.Height, encoded);
    }

    /// <summary>
    /// Decodes one face. Block data cannot be malformed past what <see cref="DdsHeader.Read"/> already checked (every
    /// block pattern decodes to some colour), so anything thrown from here is a bug in a decoder and is left to
    /// surface as one instead of passing for a damaged source.
    /// </summary>
    private static ColorRgba32[] DecodeFace(DdsHeader header, BcDecoder decoder, CompressionFormat decodeFormat, ReadOnlyMemory<byte> data)
        => header.Reader is { } reader ? reader.Unpack(data.Span, header.Width, header.Height)
            : header.Format == DxgiFormat.BC4_SNORM ? Bc4SnormDecoder.DecodeBc4(data.Span, header.Width, header.Height)
            : header.Format == DxgiFormat.BC5_SNORM ? Bc4SnormDecoder.DecodeBc5(data.Span, header.Width, header.Height)
            : decoder.DecodeRaw(data.ToArray(), header.Width, header.Height, decodeFormat);

    /// <summary>
    /// What an <c>UNKNOWN</c> target means: texconv without a format keeps the source's, so do the same when the
    /// source's format is one we can write, and fall back to plain RGBA otherwise (24-bit, palettised and signed
    /// sources have no writable twin).
    /// </summary>
    private static DxgiFormat KeepSourceFormat(DdsHeader header)
    {
        var format = header.IsSrgb ? header.Format.WithSrgb() : header.Format;
        return UncompressedWriter.Handles(format) || IsBlockTarget(format) ? format : DxgiFormat.R8G8B8A8_UNORM;
    }

    private static bool IsBlockTarget(DxgiFormat format) => format.UnormTwin() is
        DxgiFormat.BC1_UNORM or DxgiFormat.BC2_UNORM or DxgiFormat.BC3_UNORM or DxgiFormat.BC4_UNORM
        or DxgiFormat.BC5_UNORM or DxgiFormat.BC7_UNORM;

    private static bool HasCutOut(ColorRgba32[] pixels) => pixels.Any(p => p.a < 128);

    private static ReadOnlySpan<byte> Bytes(ColorRgba32[] pixels) => MemoryMarshal.AsBytes(pixels.AsSpan());

    private static ColorRgba32[] Resize(ColorRgba32[] pixels, int width, int height, int newWidth, int newHeight, bool srgb)
    {
        if (width == newWidth && height == newHeight) return pixels;

        // An sRGB source is resized in linear light on float pixels, so a 0/255 edge averages to 188 and not 128.
        // This needs Skia's RgbaF32 resize. A null from it throws like the gamma path below rather than quietly
        // resizing the stored bytes in gamma space.
        if (srgb) return ResizeLinear(pixels, width, height, newWidth, newHeight);

        using var source = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(source.GetPixelSpan());

        // Mitchell cubic is the closest Skia sampler to the Welch filter upstream uses.
        var targetInfo = new SKImageInfo(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var resized = source.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
                            ?? throw new InvalidOperationException($"Could not resize a {width}x{height} texture to {newWidth}x{newHeight}");
        return MemoryMarshal.Cast<byte, ColorRgba32>(resized.GetPixelSpan()).ToArray();
    }

    private static ColorRgba32[] ResizeLinear(ColorRgba32[] pixels, int width, int height, int newWidth, int newHeight)
    {
        using var source = new SKBitmap(new SKImageInfo(width, height, SKColorType.RgbaF32, SKAlphaType.Unpremul));
        var floats = MemoryMarshal.Cast<byte, float>(source.GetPixelSpan());
        var table = SrgbTransfer.LinearValues;
        for (var i = 0; i < pixels.Length; i++)
        {
            floats[i * 4] = (float)table[pixels[i].r];
            floats[i * 4 + 1] = (float)table[pixels[i].g];
            floats[i * 4 + 2] = (float)table[pixels[i].b];
            floats[i * 4 + 3] = pixels[i].a / 255f;
        }

        using var resized = source.Resize(new SKImageInfo(newWidth, newHeight, SKColorType.RgbaF32, SKAlphaType.Unpremul),
            new SKSamplingOptions(SKCubicResampler.Mitchell));
        if (resized is null) throw new InvalidOperationException($"Could not resize a {width}x{height} texture to {newWidth}x{newHeight}");

        var result = new ColorRgba32[newWidth * newHeight];
        var back = MemoryMarshal.Cast<byte, float>(resized.GetPixelSpan());
        for (var i = 0; i < result.Length; i++)
            result[i] = new ColorRgba32(
                SrgbTransfer.EncodeLinear(back[i * 4]), SrgbTransfer.EncodeLinear(back[i * 4 + 1]), SrgbTransfer.EncodeLinear(back[i * 4 + 2]),
                (byte)Math.Clamp(Math.Round(back[i * 4 + 3] * 255f), 0, 255));
        return result;
    }

    private static BlockFormat ToBlockFormat(DxgiFormat format) => format.UnormTwin() switch
    {
        DxgiFormat.BC1_UNORM => BlockFormat.Bc1,
        DxgiFormat.BC2_UNORM => BlockFormat.Bc2,
        DxgiFormat.BC3_UNORM => BlockFormat.Bc3,
        DxgiFormat.BC4_UNORM => BlockFormat.Bc4,
        DxgiFormat.BC5_UNORM => BlockFormat.Bc5,
        DxgiFormat.BC7_UNORM => BlockFormat.Bc7,
        _ => throw new UnsupportedTextureFormatException(
            $"cannot write {(Enum.IsDefined(format) ? format.ToString() : $"DXGI {(uint)format}")}"),
    };

    /// <summary>The decoder for the stored pixels of a source; every layout <see cref="DdsHeader"/> accepts has one.</summary>
    private static CompressionFormat ToDecodeFormat(DdsHeader header) => header.Format == DxgiFormat.BC1_UNORM && header.AlphaPixels
        ? CompressionFormat.Bc1WithAlpha
        : ToCompressionFormat(header.Format);

    private static CompressionFormat ToCompressionFormat(DxgiFormat format) => format switch
    {
        DxgiFormat.BC1_UNORM or DxgiFormat.BC1_UNORM_SRGB => CompressionFormat.Bc1,
        DxgiFormat.BC2_UNORM or DxgiFormat.BC2_UNORM_SRGB => CompressionFormat.Bc2,
        DxgiFormat.BC3_UNORM or DxgiFormat.BC3_UNORM_SRGB => CompressionFormat.Bc3,
        DxgiFormat.BC4_UNORM => CompressionFormat.Bc4,
        DxgiFormat.BC5_UNORM => CompressionFormat.Bc5,
        DxgiFormat.BC7_UNORM or DxgiFormat.BC7_UNORM_SRGB => CompressionFormat.Bc7,
        _ => throw new UnsupportedTextureFormatException(
            $"cannot write {(Enum.IsDefined(format) ? format.ToString() : $"DXGI {(uint)format}")}"),
    };
}
