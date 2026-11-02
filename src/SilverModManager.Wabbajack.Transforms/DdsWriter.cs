using System.Buffers.Binary;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Writes the DDS header and the already-encoded mip levels of a texture. The header is built here rather than by a
/// library because the pixel formats a modlist names (legacy bit masks, <c>_SRGB</c> values) are outside what the
/// library's file model can express, and because nothing may reach the output stream before every level exists.
/// </summary>
internal static class DdsWriter
{
    private const uint Magic = 0x20534444;
    private const uint HeaderSize = 124;
    private const uint PixelFormatSize = 32;

    private const uint FlagCaps = 0x1, FlagHeight = 0x2, FlagWidth = 0x4, FlagPitch = 0x8, FlagPixelFormat = 0x1000;
    private const uint FlagMipMapCount = 0x20000, FlagLinearSize = 0x80000;

    private const uint PixelFourCc = 0x4, PixelRgba = 0x41;

    private const uint CapsTexture = 0x1000, CapsComplex = 0x8, CapsMipMap = 0x400000;
    private const uint Caps2CubeMapAllFaces = 0x200 | 0xFC00;

    private const uint Dx10Texture2D = 3, Dx10MiscTextureCube = 0x4;

    private const int LegacyHeaderBytes = 128, Dx10ExtensionBytes = 20;

    /// <summary>
    /// Writes <paramref name="faces"/> (one entry per face, each holding its mip levels largest first) to
    /// <paramref name="output"/>. Six faces make a cube map.
    /// </summary>
    /// <exception cref="UnsupportedTextureFormatException"><paramref name="format"/> has no header this writer knows.</exception>
    public static void Write(Stream output, DxgiFormat format, int width, int height, IReadOnlyList<byte[][]> faces)
    {
        var layout = LayoutOf(format);
        var cubeMap = faces.Count == 6;
        var levels = faces[0].Length;
        var dx10 = layout.FourCc == "DX10";

        var header = new byte[LegacyHeaderBytes + (dx10 ? Dx10ExtensionBytes : 0)];
        void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(offset), value);

        var flags = FlagCaps | FlagHeight | FlagWidth | FlagPixelFormat | (levels > 1 ? FlagMipMapCount : 0);
        // Block formats record the size of the top level, uncompressed ones the bytes in a row.
        var pitchOrLinearSize = layout.BitCount == 0 ? (uint)faces[0][0].Length : (uint)(width * layout.BitCount / 8);
        flags |= layout.BitCount == 0 ? FlagLinearSize : FlagPitch;

        Put(0, Magic);
        Put(4, HeaderSize);
        Put(8, flags);
        Put(12, (uint)height);
        Put(16, (uint)width);
        Put(20, pitchOrLinearSize);
        Put(28, (uint)levels);
        Put(76, PixelFormatSize);
        Put(80, layout.PixelFlags);
        if (layout.FourCc.Length == 4)
            Put(84, (uint)(layout.FourCc[0] | layout.FourCc[1] << 8 | layout.FourCc[2] << 16 | layout.FourCc[3] << 24));
        Put(88, layout.BitCount);
        Put(92, layout.RMask);
        Put(96, layout.GMask);
        Put(100, layout.BMask);
        Put(104, layout.AMask);
        Put(108, CapsTexture | (levels > 1 || cubeMap ? CapsComplex : 0) | (levels > 1 ? CapsMipMap : 0));
        if (cubeMap) Put(112, Caps2CubeMapAllFaces);

        if (dx10)
        {
            Put(LegacyHeaderBytes, (uint)format);
            Put(LegacyHeaderBytes + 4, Dx10Texture2D);
            Put(LegacyHeaderBytes + 8, cubeMap ? Dx10MiscTextureCube : 0);
            Put(LegacyHeaderBytes + 12, 1);
        }

        output.Write(header);
        foreach (var face in faces)
            foreach (var level in face)
                output.Write(level);
    }

    private readonly record struct PixelLayout(uint PixelFlags, string FourCc, uint BitCount, uint RMask, uint GMask, uint BMask, uint AMask);

    private static PixelLayout LayoutOf(DxgiFormat format) => format switch
    {
        DxgiFormat.BC1_UNORM => new(PixelFourCc, "DXT1", 0, 0, 0, 0, 0),
        DxgiFormat.BC2_UNORM => new(PixelFourCc, "DXT3", 0, 0, 0, 0, 0),
        DxgiFormat.BC3_UNORM => new(PixelFourCc, "DXT5", 0, 0, 0, 0, 0),
        DxgiFormat.BC4_UNORM => new(PixelFourCc, "BC4U", 0, 0, 0, 0, 0),
        DxgiFormat.BC5_UNORM => new(PixelFourCc, "ATI2", 0, 0, 0, 0, 0),
        DxgiFormat.BC7_UNORM => new(PixelFourCc, "DX10", 0, 0, 0, 0, 0),
        DxgiFormat.R8G8B8A8_UNORM => new(PixelRgba, "", 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000),
        DxgiFormat.B8G8R8A8_UNORM => new(PixelRgba, "", 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000),
        _ => throw new UnsupportedTextureFormatException(
            $"cannot write {(Enum.IsDefined(format) ? format.ToString() : $"DXGI {(uint)format}")}"),
    };
}
