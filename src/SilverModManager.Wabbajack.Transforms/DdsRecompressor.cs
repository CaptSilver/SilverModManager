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
public static class DdsRecompressor
{
    /// <summary>
    /// Decodes the top mip of every face of <paramref name="dds"/>, resizes it to the target and writes
    /// a new DDS with the target format and mip chain to <paramref name="output"/>. Neither stream is closed.
    /// The CPU-bound decode and encode run on a thread-pool thread.
    /// </summary>
    /// <exception cref="UnsupportedTextureFormatException">The source or target format, or the face count, cannot be handled,
    /// or the source is truncated or corrupt (<see cref="UnreadableTextureException"/>). Nothing has been written to <paramref name="output"/> when it is thrown.</exception>
    public static Task Recompress(Stream dds, DdsTarget target, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.Run(() => RecompressCore(dds, target, output, token), token);
    }

    private static void RecompressCore(Stream input, DdsTarget target, Stream output, CancellationToken token)
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
        var targetFormat = packed ? CompressionFormat.Rgba : ToCompressionFormat(target.Format);

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
            faces.Add(Resize(pixels, header.Width, header.Height, target.Width, target.Height));
        }

        // BCnEncoder builds the mip chain and hands back one level at a time as RGBA. Each level then takes the
        // sRGB transfer if the source and target disagree about it, and is packed by UncompressedWriter or sent
        // through a second encoder with mip generation off. The transfer runs after the filtering, so block targets
        // match texconv, which filters in the source's space and converts at compress time.
        var mipSource = new BcEncoder
        {
            OutputOptions =
            {
                GenerateMipMaps = true,
                Format = CompressionFormat.Rgba,
                MaxMipMapLevel = target.MipLevels != 0 ? target.MipLevels : -1,
            },
        };
        var levelEncoder = new BcEncoder
        {
            OutputOptions = { Quality = CompressionQuality.Balanced, GenerateMipMaps = false, Format = targetFormat },
        };
        var transfer = header.IsSrgb == target.Format.IsSrgb() ? null : target.Format.IsSrgb() ? SrgbTransfer.ToSrgb : SrgbTransfer.ToLinear;
        var levels = mipSource.CalculateNumberOfMipLevels(target.Width, target.Height);
        if (target.MipLevels != 0) levels = Math.Min(levels, target.MipLevels);

        // Every level of every face is encoded before the first byte goes to the output, so a failure here
        // leaves the stream untouched.
        var encoded = new List<byte[][]>(faces.Count);
        foreach (var face in faces)
        {
            var faceLevels = new byte[levels][];
            for (var level = 0; level < levels; level++)
            {
                token.ThrowIfCancellationRequested();
                var rgba = mipSource.EncodeToRawBytes(Bytes(face), target.Width, target.Height, PixelFormat.Rgba32, level, out var width, out var height);
                if (transfer is not null) SrgbTransfer.Apply(rgba, transfer);
                faceLevels[level] = packed
                    ? UncompressedWriter.Pack(rgba, target.Format)
                    : levelEncoder.EncodeToRawBytes(rgba, width, height, PixelFormat.Rgba32, 0, out _, out _);
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

    private static ReadOnlySpan<byte> Bytes(ColorRgba32[] pixels) => MemoryMarshal.AsBytes(pixels.AsSpan());

    private static ColorRgba32[] Resize(ColorRgba32[] pixels, int width, int height, int newWidth, int newHeight)
    {
        if (width == newWidth && height == newHeight) return pixels;

        using var source = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        MemoryMarshal.AsBytes(pixels.AsSpan()).CopyTo(source.GetPixelSpan());

        // Mitchell cubic is the closest Skia sampler to the Welch filter upstream uses.
        var targetInfo = new SKImageInfo(newWidth, newHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var resized = source.Resize(targetInfo, new SKSamplingOptions(SKCubicResampler.Mitchell))
                            ?? throw new InvalidOperationException($"Could not resize a {width}x{height} texture to {newWidth}x{newHeight}");
        return MemoryMarshal.Cast<byte, ColorRgba32>(resized.GetPixelSpan()).ToArray();
    }

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
