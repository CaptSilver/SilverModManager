using System.Numerics;
using BCnEncoder.Shared;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Unpacks a per-pixel DDS layout to RGBA32. A layout is a pixel width plus one bit mask per channel; a channel
/// with no mask reads as 0, or 255 for alpha. Channels narrower than 8 bits are scaled up so a full channel reads
/// 255, and wider ones keep their top 8 bits, as DirectXTex does when it expands legacy layouts.
/// </summary>
internal sealed class UncompressedReader
{
    private readonly Channel _r, _g, _b, _a;

    private UncompressedReader(int bitsPerPixel, uint r, uint g, uint b, uint a)
    {
        BitsPerPixel = bitsPerPixel;
        _r = new Channel(r, 0);
        _g = new Channel(g, 0);
        _b = new Channel(b, 0);
        _a = new Channel(a, 255);
    }

    public int BitsPerPixel { get; }

    /// <summary>
    /// The layout a DX10 header names, or null when <paramref name="format"/> is not a per-pixel layout this reader
    /// handles. An <c>_SRGB</c> name maps to its UNORM twin's layout; the caller keeps the sRGB flag.
    /// </summary>
    public static UncompressedReader? FromDxgi(DxgiFormat format) => format switch
    {
        DxgiFormat.R8G8B8A8_UNORM or DxgiFormat.R8G8B8A8_UNORM_SRGB => new(32, 0xFF, 0xFF00, 0xFF0000, 0xFF000000),
        DxgiFormat.B8G8R8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM_SRGB => new(32, 0xFF0000, 0xFF00, 0xFF, 0xFF000000),
        DxgiFormat.B8G8R8X8_UNORM or DxgiFormat.B8G8R8X8_UNORM_SRGB => new(32, 0xFF0000, 0xFF00, 0xFF, 0),
        // A single channel lands in red and a second in green, so a grey mask re-encoded to BC1 comes out red, as it
        // does under DirectXTex.
        DxgiFormat.R8_UNORM => new(8, 0xFF, 0, 0, 0),
        DxgiFormat.R8G8_UNORM => new(16, 0xFF, 0xFF00, 0, 0),
        _ => null,
    };

    private enum Kind { Rgb, Luminance, Alpha }

    /// <summary>One row of DirectXTex's legacy pixel-format table: what the header says, and how to read it.</summary>
    private sealed record Row(Kind Kind, int Bits, uint R, uint G, uint B, uint A, DxgiFormat Dxgi, UncompressedReader Reader)
    {
        public Row(Kind kind, int bits, uint r, uint g, uint b, uint a, DxgiFormat dxgi)
            : this(kind, bits, r, g, b, a, dxgi, new UncompressedReader(bits, r, g, b, a)) { }
    }

    private const uint FlagAlpha = 0x2;
    private const uint FlagRgb = 0x40;
    private const uint FlagLuminance = 0x20000;

    private static readonly Row[] Rows =
    [
        new(Kind.Rgb, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, DxgiFormat.B8G8R8A8_UNORM),  // A8R8G8B8
        new(Kind.Rgb, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000, DxgiFormat.R8G8B8A8_UNORM),  // A8B8G8R8
        new(Kind.Rgb, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0, DxgiFormat.B8G8R8X8_UNORM),           // X8R8G8B8
        new(Kind.Rgb, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0, DxgiFormat.R8G8B8A8_UNORM),           // X8B8G8R8
        new(Kind.Rgb, 32, 0x0000FFFF, 0xFFFF0000, 0, 0, DxgiFormat.UNKNOWN),                           // G16R16
        new(Kind.Rgb, 32, 0x000003FF, 0x000FFC00, 0x3FF00000, 0xC0000000, DxgiFormat.UNKNOWN),         // A2B10G10R10
        new(Kind.Rgb, 32, 0x3FF00000, 0x000FFC00, 0x000003FF, 0xC0000000, DxgiFormat.UNKNOWN),         // A2R10G10B10
        new(Kind.Rgb, 24, 0xFF0000, 0x00FF00, 0x0000FF, 0, DxgiFormat.UNKNOWN),                        // R8G8B8
        new(Kind.Rgb, 16, 0xF800, 0x07E0, 0x001F, 0, DxgiFormat.B5G6R5_UNORM),                         // R5G6B5
        new(Kind.Rgb, 16, 0x7C00, 0x03E0, 0x001F, 0x8000, DxgiFormat.B5G5R5A1_UNORM),                  // A1R5G5B5
        new(Kind.Rgb, 16, 0x7C00, 0x03E0, 0x001F, 0, DxgiFormat.B5G5R5A1_UNORM),                       // X1R5G5B5
        new(Kind.Rgb, 16, 0x0F00, 0x00F0, 0x000F, 0xF000, DxgiFormat.B4G4R4A4_UNORM),                  // A4R4G4B4
        new(Kind.Rgb, 16, 0x0F00, 0x00F0, 0x000F, 0, DxgiFormat.B4G4R4A4_UNORM),                       // X4R4G4B4
        new(Kind.Rgb, 16, 0x00E0, 0x001C, 0x0003, 0xFF00, DxgiFormat.UNKNOWN),                         // A8R3G3B2
        new(Kind.Rgb, 8, 0xE0, 0x1C, 0x03, 0, DxgiFormat.UNKNOWN),                                     // R3G3B2
        new(Kind.Luminance, 8, 0xFF, 0, 0, 0, DxgiFormat.R8_UNORM),                                    // L8
        new(Kind.Luminance, 16, 0xFFFF, 0, 0, 0, DxgiFormat.R16_UNORM),                                // L16
        // A8L8 reads as (L, A, 0, 255), the way DirectXTex's R8G8 conversion of it does.
        new(Kind.Luminance, 16, 0x00FF, 0, 0, 0xFF00, DxgiFormat.R8G8_UNORM, new UncompressedReader(16, 0x00FF, 0xFF00, 0, 0)),
        // A4L4 spreads the luminance over RGB, as DirectXTex expands it to B4G4R4A4.
        new(Kind.Luminance, 8, 0x0F, 0, 0, 0xF0, DxgiFormat.B4G4R4A4_UNORM, new UncompressedReader(8, 0x0F, 0x0F, 0x0F, 0xF0)),
        new(Kind.Alpha, 8, 0, 0, 0, 0xFF, DxgiFormat.A8_UNORM),                                        // A8
    ];

    /// <summary>
    /// Finds the row of the legacy table that a no-FourCC pixel format matches, comparing the kind of data, the
    /// pixel width and all four masks. Null when the table has no such layout.
    /// </summary>
    public static (UncompressedReader Reader, DxgiFormat Dxgi)? FromBitmasks(uint pixelFlags, uint bitCount, uint r, uint g, uint b, uint a)
    {
        var kind = (pixelFlags & FlagRgb) != 0 ? Kind.Rgb
            : (pixelFlags & FlagLuminance) != 0 ? Kind.Luminance
            : (pixelFlags & FlagAlpha) != 0 ? Kind.Alpha
            : (Kind?)null;
        if (kind is null) return null;

        foreach (var row in Rows)
            if (row.Kind == kind && row.Bits == bitCount && row.R == r && row.G == g && row.B == b && row.A == a)
                return (row.Reader, row.Dxgi);
        return null;
    }

    /// <summary>Unpacks one mip of one face; <paramref name="data"/> holds exactly that level's rows.</summary>
    public ColorRgba32[] Unpack(ReadOnlySpan<byte> data, int width, int height)
    {
        var bytesPerPixel = BitsPerPixel / 8;
        var pitch = (long)width * bytesPerPixel;
        if (data.Length < pitch * height) throw new InvalidDataException("A DDS level ends before its pixel data does");

        var pixels = new ColorRgba32[width * height];
        for (var y = 0; y < height; y++)
        {
            var row = data.Slice((int)(y * pitch), (int)pitch);
            for (var x = 0; x < width; x++)
            {
                uint value = 0;
                for (var i = 0; i < bytesPerPixel; i++) value |= (uint)row[x * bytesPerPixel + i] << (8 * i);
                pixels[y * width + x] = new ColorRgba32(_r.Read(value), _g.Read(value), _b.Read(value), _a.Read(value));
            }
        }

        return pixels;
    }

    private readonly struct Channel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly int _bits;
        private readonly byte _absent;

        public Channel(uint mask, byte absent)
        {
            _mask = mask;
            _absent = absent;
            if (mask == 0) return;
            _shift = BitOperations.TrailingZeroCount(mask);
            _bits = BitOperations.PopCount(mask);
        }

        public byte Read(uint pixel)
        {
            if (_mask == 0) return _absent;
            var value = (pixel & _mask) >> _shift;
            if (_bits == 8) return (byte)value;
            if (_bits > 8) return (byte)(value >> (_bits - 8));
            var max = (1u << _bits) - 1;
            return (byte)((value * 255 + max / 2) / max);
        }
    }
}
