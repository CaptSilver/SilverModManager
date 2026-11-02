using System.Buffers.Binary;
using System.Globalization;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// The parts of a DDS header the recompressor needs: size, mip count, face count, pixel layout and where the
/// pixel data starts. Written against DirectXTex's reader so a texture the tools agree on reads the same here.
/// </summary>
internal sealed class DdsHeader
{
    private const uint Magic = 0x20534444; // "DDS "
    private const int HeaderSize = 124;
    private const int PixelDataOffset = 4 + HeaderSize;
    private const int Dx10ExtensionSize = 20;


    private const uint PixelAlphaPixels = 0x1;
    private const uint PixelFourCc = 0x4;
    private const uint PixelPalette = 0x20;
    private const uint PixelPaletteAlpha = 0x21;

    private const uint CapsCubeMap = 0x200;
    private const uint CapsCubeMapAllFaces = 0xFC00;
    private const uint CapsVolume = 0x200000;

    private const uint Dx10Texture2D = 3;
    private const uint Dx10MiscCubeMap = 0x4;

    private DdsHeader(int width, int height, int mipLevels, int faceCount, DxgiFormat format, bool isSrgb, UncompressedReader? reader, bool alphaPixels, int dataOffset)
    {
        IsSrgb = isSrgb;
        Reader = reader;
        Width = width;
        Height = height;
        MipLevels = mipLevels;
        FaceCount = faceCount;
        Format = format;
        AlphaPixels = alphaPixels;
        DataOffset = dataOffset;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The number of levels stored per face; a header that records none holds one.</summary>
    public int MipLevels { get; }

    /// <summary>1 for a plain texture, 6 for a cube map.</summary>
    public int FaceCount { get; }

    /// <summary>The layout of the stored pixels, as the DXGI format that describes it.</summary>
    public DxgiFormat Format { get; }

    /// <summary>The DX10 header names an <c>_SRGB</c> format. <see cref="Format"/> then holds the UNORM twin, which decodes the same bytes.</summary>
    public bool IsSrgb { get; }

    /// <summary>Unpacks the pixels of a per-pixel layout; null for a block-compressed source.</summary>
    public UncompressedReader? Reader { get; }

    /// <summary>A legacy BC1 header that says the colour data carries alpha, so 3-colour blocks mean transparent.</summary>
    public bool AlphaPixels { get; }

    /// <summary>Offset of the first byte of pixel data.</summary>
    public int DataOffset { get; }

    public bool IsBlockCompressed => Reader is null;

    /// <summary>Bytes per 4x4 block, or 0 for a format stored per pixel.</summary>
    public static int BlockBytes(DxgiFormat format) => format switch
    {
        DxgiFormat.BC1_UNORM or DxgiFormat.BC4_UNORM or DxgiFormat.BC4_SNORM => 8,
        DxgiFormat.BC2_UNORM or DxgiFormat.BC3_UNORM or DxgiFormat.BC5_UNORM or DxgiFormat.BC5_SNORM or DxgiFormat.BC7_UNORM => 16,
        _ => 0,
    };

    /// <summary>Bytes in one row of blocks (or pixels) of mip <paramref name="level"/>; DirectXTex <c>ComputePitch</c>.</summary>
    public long RowPitch(int level)
    {
        var width = Math.Max(1, Width >> level);
        return Reader is { } reader
            ? ((long)width * reader.BitsPerPixel + 7) / 8
            : (long)Math.Max(1, (width + 3) / 4) * BlockBytes(Format);
    }

    /// <summary>Bytes of mip <paramref name="level"/> of one face.</summary>
    public long LevelSize(int level)
    {
        var height = Math.Max(1, Height >> level);
        return RowPitch(level) * (IsBlockCompressed ? Math.Max(1, (height + 3) / 4) : height);
    }

    /// <summary>Bytes of every mip of one face; faces follow each other in the file at this stride.</summary>
    public long FaceSize
    {
        get
        {
            long size = 0;
            for (var level = 0; level < MipLevels; level++) size += LevelSize(level);
            return size;
        }
    }

    /// <summary>Offset of mip <paramref name="level"/> of face <paramref name="face"/>.</summary>
    public long LevelOffset(int face, int level)
    {
        var offset = TopLevelOffset(face);
        for (var i = 0; i < level; i++) offset += LevelSize(i);
        return offset;
    }

    /// <summary>Offset of the top mip of face <paramref name="face"/>.</summary>
    public long TopLevelOffset(int face) => DataOffset + face * FaceSize;

    /// <summary>
    /// Reads the header of <paramref name="dds"/>.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a DDS header, or the pixel data is shorter than the header says.</exception>
    /// <exception cref="UnsupportedTextureFormatException">The layout, dimension or face count cannot be decoded; the message prints the header fields.</exception>
    public static DdsHeader Read(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < PixelDataOffset || U32(dds, 0) != Magic || U32(dds, 4) != HeaderSize || U32(dds, 76) != 32)
            throw new InvalidDataException("Not a DDS file");

        var height = U32(dds, 12);
        var width = U32(dds, 16);
        // DirectXTex reads the count whatever the flags say, and writers differ on setting the flag.
        var mipCount = U32(dds, 28);
        var pixelFlags = U32(dds, 80);
        var fourCc = U32(dds, 84);
        var bitCount = U32(dds, 88);
        var masks = (R: U32(dds, 92), G: U32(dds, 96), B: U32(dds, 100), A: U32(dds, 104));
        var caps2 = U32(dds, 112);

        var hasDx10 = (pixelFlags & PixelFourCc) != 0 && fourCc == FourCc("DX10");
        uint dxgi = 0;
        var dataOffset = PixelDataOffset;
        var faces = (caps2 & CapsCubeMap) != 0 ? 6 : 1;

        // BC6H has a DXGI name even in a legacy header; print it so the message reads like the DX10 one.
        if (!hasDx10 && (pixelFlags & PixelFourCc) != 0 && fourCc == FourCc("BC6H")) dxgi = (uint)DxgiFormat.BC6H_UF16;

        string Fields() => DescribeFields(pixelFlags, fourCc, bitCount, masks, dxgi);

        if (hasDx10)
        {
            if (dds.Length < PixelDataOffset + Dx10ExtensionSize) throw new InvalidDataException("A DDS file ends inside its DX10 header");
            dxgi = U32(dds, 128);
            var dimension = U32(dds, 132);
            var misc = U32(dds, 136);
            var arraySize = U32(dds, 140);
            dataOffset += Dx10ExtensionSize;
            if (dimension != Dx10Texture2D || arraySize != 1)
                throw new UnsupportedTextureFormatException($"cannot read the source texture: it is not a single 2D texture or cube map (dimension={dimension}, arraySize={arraySize}); {Fields()}");
            faces = (misc & Dx10MiscCubeMap) != 0 ? 6 : 1;
        }
        else if ((caps2 & CapsVolume) != 0)
        {
            throw new UnsupportedTextureFormatException($"cannot read the source texture: it is a 3D texture; {Fields()}");
        }

        if (faces == 6 && !hasDx10 && (caps2 & CapsCubeMapAllFaces) != CapsCubeMapAllFaces)
            throw new UnsupportedTextureFormatException($"cannot read the source texture: it is a cube map with missing faces; {Fields()}");

        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            throw new InvalidDataException($"A DDS file records a {width}x{height} texture");

        var isPaletted = !hasDx10 && ((pixelFlags == PixelPalette && bitCount == 8) || (pixelFlags == PixelPaletteAlpha && bitCount == 16));
        if (isPaletted)
        {
            // The colour table sits between the header and the pixels.
            if (dds.Length < dataOffset + UncompressedReader.PaletteBytes) throw new InvalidDataException("A DDS file ends inside its palette");
            dataOffset += UncompressedReader.PaletteBytes;
        }

        var source = hasDx10
            ? FromDx10(dxgi)
            : isPaletted
                ? (DxgiFormat.R8G8B8A8_UNORM, false, UncompressedReader.Paletted(dds.Slice(PixelDataOffset, UncompressedReader.PaletteBytes), alphaInPixel: bitCount == 16))
                : FromLegacy(pixelFlags, fourCc, bitCount, masks);
        if (source is not var (known, srgb, reader))
            throw new UnsupportedTextureFormatException($"cannot read the source texture: {Fields()}");

        var header = new DdsHeader((int)width, (int)height, (int)Math.Max(1, mipCount), faces, known, srgb, reader,
            alphaPixels: !hasDx10 && known == DxgiFormat.BC1_UNORM && (pixelFlags & PixelAlphaPixels) != 0, dataOffset);

        var lastFaceEnd = header.TopLevelOffset(faces - 1) + header.LevelSize(0);
        if (dds.Length < lastFaceEnd) throw new InvalidDataException("A DDS file ends before its pixel data does");
        return header;
    }

    private static (DxgiFormat Format, bool Srgb, UncompressedReader? Reader)? FromDx10(uint dxgi)
    {
        var format = (DxgiFormat)dxgi;
        var srgb = format is DxgiFormat.BC1_UNORM_SRGB or DxgiFormat.BC2_UNORM_SRGB or DxgiFormat.BC3_UNORM_SRGB
            or DxgiFormat.BC7_UNORM_SRGB or DxgiFormat.R8G8B8A8_UNORM_SRGB or DxgiFormat.B8G8R8A8_UNORM_SRGB
            or DxgiFormat.B8G8R8X8_UNORM_SRGB;

        // The _SRGB block formats store the same bytes as their UNORM twins; only the transfer function differs.
        var unorm = format switch
        {
            DxgiFormat.BC1_UNORM_SRGB => DxgiFormat.BC1_UNORM,
            DxgiFormat.BC2_UNORM_SRGB => DxgiFormat.BC2_UNORM,
            DxgiFormat.BC3_UNORM_SRGB => DxgiFormat.BC3_UNORM,
            DxgiFormat.BC7_UNORM_SRGB => DxgiFormat.BC7_UNORM,
            DxgiFormat.R8G8B8A8_UNORM_SRGB => DxgiFormat.R8G8B8A8_UNORM,
            DxgiFormat.B8G8R8A8_UNORM_SRGB => DxgiFormat.B8G8R8A8_UNORM,
            DxgiFormat.B8G8R8X8_UNORM_SRGB => DxgiFormat.B8G8R8X8_UNORM,
            _ => format,
        };

        if (unorm is DxgiFormat.BC1_UNORM or DxgiFormat.BC2_UNORM or DxgiFormat.BC3_UNORM or DxgiFormat.BC4_UNORM
            or DxgiFormat.BC4_SNORM or DxgiFormat.BC5_UNORM or DxgiFormat.BC5_SNORM or DxgiFormat.BC7_UNORM)
            return (unorm, srgb, null);

        return UncompressedReader.FromDxgi(unorm) is { } reader ? (unorm, srgb, reader) : null;
    }

    /// <summary>Block-compressed FourCCs, as DirectXTex's legacy table maps them.</summary>
    private static readonly Dictionary<uint, DxgiFormat> FourCcFormats = new()
    {
        [FourCc("DXT1")] = DxgiFormat.BC1_UNORM,
        [FourCc("DXT3")] = DxgiFormat.BC2_UNORM,
        [FourCc("DXT5")] = DxgiFormat.BC3_UNORM,
        // DXT2 and DXT4 store premultiplied alpha. They are read as stored, with straight alpha and no
        // un-premultiplying, which is what texconv does unless it is asked for -alpha.
        [FourCc("DXT2")] = DxgiFormat.BC2_UNORM,
        [FourCc("DXT4")] = DxgiFormat.BC3_UNORM,
        // Swizzled normal-map variants of DXT5. DirectXTex hands them back as BC3 without un-swizzling, and so do we.
        [FourCc("A2D5")] = DxgiFormat.BC3_UNORM,
        [FourCc("xGBR")] = DxgiFormat.BC3_UNORM,
        [FourCc("RxBG")] = DxgiFormat.BC3_UNORM,
        [FourCc("RBxG")] = DxgiFormat.BC3_UNORM,
        [FourCc("xRBG")] = DxgiFormat.BC3_UNORM,
        [FourCc("RGxB")] = DxgiFormat.BC3_UNORM,
        [FourCc("xGxR")] = DxgiFormat.BC3_UNORM,
        [FourCc("GXRB")] = DxgiFormat.BC3_UNORM,
        [FourCc("GRXB")] = DxgiFormat.BC3_UNORM,
        [FourCc("RXGB")] = DxgiFormat.BC3_UNORM,
        [FourCc("BRGX")] = DxgiFormat.BC3_UNORM,
        [FourCc("ATI1")] = DxgiFormat.BC4_UNORM,
        [FourCc("BC4U")] = DxgiFormat.BC4_UNORM,
        [FourCc("BC4S")] = DxgiFormat.BC4_SNORM,
        [FourCc("ATI2")] = DxgiFormat.BC5_UNORM,
        [FourCc("BC5U")] = DxgiFormat.BC5_UNORM,
        [FourCc("A2XY")] = DxgiFormat.BC5_UNORM,
        [FourCc("BC5S")] = DxgiFormat.BC5_SNORM,
        [FourCc("BC7L")] = DxgiFormat.BC7_UNORM,
        [FourCc("BC7\0")] = DxgiFormat.BC7_UNORM,
    };

    /// <summary>FourCC values that name a D3DFMT this reader does not decode, so the message can say which.</summary>
    private static readonly Dictionary<uint, string> UnsupportedFourCcs = new()
    {
        [36] = "D3DFMT_A16B16G16R16",
        [60] = "D3DFMT_V8U8",
        [61] = "D3DFMT_L6V5U5",
        [62] = "D3DFMT_X8L8V8U8",
        [63] = "D3DFMT_Q8W8V8U8",
        [64] = "D3DFMT_V16U16",
        [67] = "D3DFMT_A2W10V10U10",
        [110] = "D3DFMT_Q16W16V16U16",
        [111] = "D3DFMT_R16F",
        [112] = "D3DFMT_G16R16F",
        [113] = "D3DFMT_A16B16G16R16F",
        [114] = "D3DFMT_R32F",
        [115] = "D3DFMT_G32R32F",
        [116] = "D3DFMT_A32B32G32R32F",
        [FourCc("RGBG")] = "D3DFMT_R8G8_B8G8",
        [FourCc("GRGB")] = "D3DFMT_G8R8_G8B8",
        [FourCc("YUY2")] = "D3DFMT_YUY2",
        [FourCc("UYVY")] = "D3DFMT_UYVY",
    };

    private static (DxgiFormat Format, bool Srgb, UncompressedReader? Reader)? FromLegacy(uint pixelFlags, uint fourCc, uint bitCount, (uint R, uint G, uint B, uint A) masks)
    {
        if ((pixelFlags & PixelFourCc) != 0)
            return FourCcFormats.TryGetValue(fourCc, out var format) ? (format, false, null) : null;

        return UncompressedReader.FromBitmasks(pixelFlags, bitCount, masks.R, masks.G, masks.B, masks.A) is var (reader, dxgi)
            ? (dxgi, false, reader)
            : null;
    }

    /// <summary>The header fields the message prints, so the next layout nobody handled can be named from the panel.</summary>
    private static string DescribeFields(uint pixelFlags, uint fourCc, uint bitCount, (uint R, uint G, uint B, uint A) masks, uint dxgi)
    {
        var dxgiName = Enum.IsDefined((DxgiFormat)dxgi) ? $"{(DxgiFormat)dxgi} ({dxgi})" : dxgi.ToString(CultureInfo.InvariantCulture);
        return $"fourcc={FourCcText(fourCc)}, flags=0x{pixelFlags:X}, bpp={bitCount}, masks=0x{masks.R:X}/0x{masks.G:X}/0x{masks.B:X}/0x{masks.A:X}, dxgi={dxgiName}";
    }

    private static string FourCcText(uint fourCc)
    {
        if (fourCc == 0) return "none";
        var named = UnsupportedFourCcs.TryGetValue(fourCc, out var name) ? $" ({name})" : "";
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, fourCc);
        foreach (var b in bytes)
            if (b is < 0x20 or > 0x7E) return fourCc.ToString(CultureInfo.InvariantCulture) + named;
        return System.Text.Encoding.ASCII.GetString(bytes) + named;
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static uint FourCc(string text) => (uint)(text[0] | text[1] << 8 | text[2] << 16 | text[3] << 24);
}
