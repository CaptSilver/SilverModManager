namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Packs RGBA32 pixels into the per-pixel layouts a modlist can name as a target. Each layout is the inverse of the
/// matching row <see cref="UncompressedReader"/> reads, so a texture written here reads back the way DirectXTex
/// would read it. Channels a layout lacks are dropped, narrower ones are rounded, and an unused byte is written as 255.
/// </summary>
internal static class UncompressedWriter
{
    /// <summary>Bits per pixel of <paramref name="format"/>, or 0 when it is not a layout this writer packs.</summary>
    public static int BitsPerPixel(DxgiFormat format) => format.UnormTwin() switch
    {
        DxgiFormat.R8G8B8A8_UNORM or DxgiFormat.B8G8R8A8_UNORM or DxgiFormat.B8G8R8X8_UNORM => 32,
        DxgiFormat.R8G8_UNORM or DxgiFormat.B5G6R5_UNORM or DxgiFormat.B5G5R5A1_UNORM or DxgiFormat.B4G4R4A4_UNORM or DxgiFormat.R16_UNORM => 16,
        DxgiFormat.R8_UNORM or DxgiFormat.A8_UNORM => 8,
        _ => 0,
    };

    public static bool Handles(DxgiFormat format) => BitsPerPixel(format) != 0;

    /// <summary>Packs one mip level of tightly packed RGBA bytes. Rows are not padded.</summary>
    /// <exception cref="UnsupportedTextureFormatException"><paramref name="format"/> is not a layout this writer packs.</exception>
    public static byte[] Pack(ReadOnlySpan<byte> rgba, DxgiFormat format)
    {
        var bits = BitsPerPixel(format);
        if (bits == 0)
            throw new UnsupportedTextureFormatException(
                $"cannot write {(Enum.IsDefined(format) ? format.ToString() : $"DXGI {(uint)format}")}");

        var pixels = rgba.Length / 4;
        var packed = new byte[pixels * bits / 8];
        var twin = format.UnormTwin();
        for (var i = 0; i < pixels; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3];
            var at = i * bits / 8;
            switch (twin)
            {
                case DxgiFormat.R8G8B8A8_UNORM: packed[at] = r; packed[at + 1] = g; packed[at + 2] = b; packed[at + 3] = a; break;
                case DxgiFormat.B8G8R8A8_UNORM: packed[at] = b; packed[at + 1] = g; packed[at + 2] = r; packed[at + 3] = a; break;
                case DxgiFormat.B8G8R8X8_UNORM: packed[at] = b; packed[at + 1] = g; packed[at + 2] = r; packed[at + 3] = 255; break;
                case DxgiFormat.R8_UNORM: packed[at] = r; break;
                // Green rides in the high byte, which the A8L8 header calls alpha.
                case DxgiFormat.R8G8_UNORM: packed[at] = r; packed[at + 1] = g; break;
                case DxgiFormat.A8_UNORM: packed[at] = a; break;
                case DxgiFormat.R16_UNORM: Put16(packed, at, (ushort)(r * 257)); break;
                case DxgiFormat.B5G6R5_UNORM: Put16(packed, at, (ushort)(Scale(r, 31) << 11 | Scale(g, 63) << 5 | Scale(b, 31))); break;
                case DxgiFormat.B5G5R5A1_UNORM: Put16(packed, at, (ushort)(Scale(r, 31) << 10 | Scale(g, 31) << 5 | Scale(b, 31) | (a >= 128 ? 0x8000 : 0))); break;
                case DxgiFormat.B4G4R4A4_UNORM: Put16(packed, at, (ushort)(Scale(r, 15) << 8 | Scale(g, 15) << 4 | Scale(b, 15) | Scale(a, 15) << 12)); break;
            }
        }

        return packed;
    }

    private static int Scale(byte value, int max) => (value * max + 127) / 255;

    private static void Put16(byte[] destination, int at, ushort value)
    {
        destination[at] = (byte)value;
        destination[at + 1] = (byte)(value >> 8);
    }
}
