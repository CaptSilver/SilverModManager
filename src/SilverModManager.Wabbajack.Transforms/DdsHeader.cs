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

    private const uint FlagMipMapCount = 0x20000;

    private const uint PixelAlphaPixels = 0x1;
    private const uint PixelFourCc = 0x4;
    private const uint PixelRgb = 0x40;

    private const uint CapsCubeMap = 0x200;
    private const uint CapsCubeMapAllFaces = 0xFC00;
    private const uint CapsVolume = 0x200000;

    private const uint Dx10Texture2D = 3;
    private const uint Dx10MiscCubeMap = 0x4;

    private DdsHeader(int width, int height, int mipLevels, int faceCount, DxgiFormat format, bool alphaPixels, int dataOffset)
    {
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

    /// <summary>A legacy BC1 header that says the colour data carries alpha, so 3-colour blocks mean transparent.</summary>
    public bool AlphaPixels { get; }

    /// <summary>Offset of the first byte of pixel data.</summary>
    public int DataOffset { get; }

    public bool IsBlockCompressed => BlockBytes(Format) > 0;

    /// <summary>Bytes per 4x4 block, or 0 for a format stored per pixel.</summary>
    public static int BlockBytes(DxgiFormat format) => format switch
    {
        DxgiFormat.BC1_UNORM or DxgiFormat.BC4_UNORM => 8,
        DxgiFormat.BC2_UNORM or DxgiFormat.BC3_UNORM or DxgiFormat.BC5_UNORM or DxgiFormat.BC7_UNORM => 16,
        _ => 0,
    };

    private static int BitsPerPixel(DxgiFormat format) => format switch
    {
        DxgiFormat.R8G8B8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM => 32,
        _ => 0,
    };

    /// <summary>Bytes in one row of blocks (or pixels) of mip <paramref name="level"/>; DirectXTex <c>ComputePitch</c>.</summary>
    public long RowPitch(int level)
    {
        var width = Math.Max(1, Width >> level);
        return IsBlockCompressed
            ? (long)Math.Max(1, (width + 3) / 4) * BlockBytes(Format)
            : ((long)width * BitsPerPixel(Format) + 7) / 8;
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

        var flags = U32(dds, 8);
        var height = U32(dds, 12);
        var width = U32(dds, 16);
        var mipCount = (flags & FlagMipMapCount) != 0 ? U32(dds, 28) : 0;
        var pixelFlags = U32(dds, 80);
        var fourCc = U32(dds, 84);
        var bitCount = U32(dds, 88);
        var masks = (R: U32(dds, 92), G: U32(dds, 96), B: U32(dds, 100), A: U32(dds, 104));
        var caps2 = U32(dds, 112);

        var hasDx10 = (pixelFlags & PixelFourCc) != 0 && fourCc == FourCc("DX10");
        uint dxgi = 0;
        var dataOffset = PixelDataOffset;
        var faces = (caps2 & CapsCubeMap) != 0 ? 6 : 1;

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

        var format = hasDx10
            ? FromDx10(dxgi)
            : FromLegacy(pixelFlags, fourCc, bitCount, masks);
        if (format is not { } known)
            throw new UnsupportedTextureFormatException($"cannot read the source texture: {Fields()}");

        var header = new DdsHeader((int)width, (int)height, (int)Math.Max(1, mipCount), faces, known,
            alphaPixels: !hasDx10 && known == DxgiFormat.BC1_UNORM && (pixelFlags & PixelAlphaPixels) != 0, dataOffset);

        var lastFaceEnd = header.TopLevelOffset(faces - 1) + header.LevelSize(0);
        if (dds.Length < lastFaceEnd) throw new InvalidDataException("A DDS file ends before its pixel data does");
        return header;
    }

    private static DxgiFormat? FromDx10(uint dxgi)
    {
        var format = (DxgiFormat)dxgi;
        return format is DxgiFormat.BC1_UNORM or DxgiFormat.BC2_UNORM or DxgiFormat.BC3_UNORM or DxgiFormat.BC4_UNORM
            or DxgiFormat.BC5_UNORM or DxgiFormat.BC7_UNORM or DxgiFormat.R8G8B8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM
            ? format
            : null;
    }

    private static DxgiFormat? FromLegacy(uint pixelFlags, uint fourCc, uint bitCount, (uint R, uint G, uint B, uint A) masks)
    {
        if ((pixelFlags & PixelFourCc) != 0)
        {
            // BC4S and BC5S are signed; they are read as their unsigned twins until a signed decoder exists.
            if (fourCc == FourCc("DXT1")) return DxgiFormat.BC1_UNORM;
            if (fourCc == FourCc("DXT3")) return DxgiFormat.BC2_UNORM;
            if (fourCc == FourCc("DXT5")) return DxgiFormat.BC3_UNORM;
            if (fourCc == FourCc("ATI1") || fourCc == FourCc("BC4U") || fourCc == FourCc("BC4S")) return DxgiFormat.BC4_UNORM;
            if (fourCc == FourCc("ATI2") || fourCc == FourCc("BC5U") || fourCc == FourCc("BC5S")) return DxgiFormat.BC5_UNORM;
            return null;
        }

        if ((pixelFlags & PixelRgb) != 0 && (pixelFlags & PixelAlphaPixels) != 0 && bitCount == 32 && masks.A == 0xFF000000)
        {
            if (masks is { R: 0x000000FF, G: 0x0000FF00, B: 0x00FF0000 }) return DxgiFormat.R8G8B8A8_UNORM;
            if (masks is { R: 0x00FF0000, G: 0x0000FF00, B: 0x000000FF }) return DxgiFormat.B8G8R8A8_UNORM;
        }

        return null;
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
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, fourCc);
        foreach (var b in bytes)
            if (b is < 0x20 or > 0x7E) return fourCc.ToString(CultureInfo.InvariantCulture);
        return System.Text.Encoding.ASCII.GetString(bytes);
    }

    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);

    private static uint FourCc(string text) => (uint)(text[0] | text[1] << 8 | text[2] << 16 | text[3] << 24);
}
