// Follows Wabbajack.Hashing.PHash/CrossPlatformImageLoader.cs, with SkiaSharp doing the resize.
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using BCnEncoder.Shared.ImageFiles;
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
    /// <exception cref="UnsupportedTextureFormatException">The source or target format, or the face count, cannot be handled.</exception>
    public static Task Recompress(Stream dds, DdsTarget target, Stream output, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.Run(() => RecompressCore(dds, target, output, token), token);
    }

    private static void RecompressCore(Stream input, DdsTarget target, Stream output, CancellationToken token)
    {
        // Check the target before the expensive decode.
        var targetFormat = ToCompressionFormat(target.Format);

        var file = DdsFile.Load(input);
        var sourceFormat = file.dx10Header.dxgiFormat == BCnEncoder.Shared.ImageFiles.DxgiFormat.DxgiFormatUnknown
            ? file.header.ddsPixelFormat.DxgiFormat
            : file.dx10Header.dxgiFormat;
        var decodeFormat = ToCompressionFormat((DxgiFormat)(uint)sourceFormat);

        if (file.Faces.Count is not (1 or 6))
            throw new UnsupportedTextureFormatException($"Can't encode a DDS with {file.Faces.Count} faces");

        var decoder = new BcDecoder();
        var faces = new List<ColorRgba32[]>(file.Faces.Count);
        foreach (var face in file.Faces)
        {
            token.ThrowIfCancellationRequested();
            var pixels = decoder.DecodeRaw(face.MipMaps[0].Data, (int)face.Width, (int)face.Height, decodeFormat);
            faces.Add(Resize(pixels, (int)face.Width, (int)face.Height, target.Width, target.Height));
        }

        var encoder = new BcEncoder
        {
            OutputOptions =
            {
                Quality = CompressionQuality.Balanced,
                GenerateMipMaps = true,
                Format = targetFormat,
                FileFormat = OutputFileFormat.Dds,
                MaxMipMapLevel = target.MipLevels != 0 ? target.MipLevels : -1,
            },
        };

        token.ThrowIfCancellationRequested();
        var result = faces.Count == 1
            ? encoder.EncodeToDds(Bytes(faces[0]), target.Width, target.Height, PixelFormat.Rgba32)
            : encoder.EncodeCubeMapToDds(
                Bytes(faces[0]), Bytes(faces[1]), Bytes(faces[2]), Bytes(faces[3]), Bytes(faces[4]), Bytes(faces[5]),
                target.Width, target.Height, PixelFormat.Rgba32);
        result.Write(output);
    }

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

    private static CompressionFormat ToCompressionFormat(DxgiFormat format) => format switch
    {
        DxgiFormat.BC1_UNORM => CompressionFormat.Bc1,
        DxgiFormat.BC2_UNORM => CompressionFormat.Bc2,
        DxgiFormat.BC3_UNORM => CompressionFormat.Bc3,
        DxgiFormat.BC4_UNORM => CompressionFormat.Bc4,
        DxgiFormat.BC5_UNORM => CompressionFormat.Bc5,
        DxgiFormat.BC7_UNORM => CompressionFormat.Bc7,
        DxgiFormat.B8G8R8A8_UNORM => CompressionFormat.Bgra,
        DxgiFormat.R8G8B8A8_UNORM => CompressionFormat.Rgba,
        _ => throw new UnsupportedTextureFormatException(
            $"Cannot re-encode a texture with format {(Enum.IsDefined(format) ? format.ToString() : $"DXGI {(uint)format}")}"),
    };
}
