using BCnEncoder.Shared;

namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// Decodes BC4 and BC5 blocks whose channels are signed. BCnEncoder only knows the unsigned layout, and reading a
/// signed block as unsigned is wrong in more than the sign: the endpoint comparison that picks the 6- or
/// 4-interpolant table and the interpolation itself are both signed. Results are mapped to UNORM the way
/// DirectXTex maps SNORM, <c>max(v / 127, -1)</c> then <c>(v * 0.5 + 0.5) * 255</c>.
/// </summary>
internal static class Bc4SnormDecoder
{
    private const int BlockSide = 4;
    private const int ChannelBlockBytes = 8;

    /// <summary>Decodes a BC4 level to <c>(R, 0, 0, 255)</c> pixels.</summary>
    public static ColorRgba32[] DecodeBc4(ReadOnlySpan<byte> blocks, int width, int height) => Decode(blocks, width, height, channels: 1);

    /// <summary>Decodes a BC5 level, two BC4 blocks per block, to <c>(R, G, 0, 255)</c> pixels.</summary>
    public static ColorRgba32[] DecodeBc5(ReadOnlySpan<byte> blocks, int width, int height) => Decode(blocks, width, height, channels: 2);

    /// <summary>The 16 UNORM values of one signed 8-byte channel block, row-major.</summary>
    public static byte[] DecodeChannelBlock(ReadOnlySpan<byte> block)
    {
        var palette = Palette(block);
        var indices = 0UL;
        for (var i = 0; i < 6; i++) indices |= (ulong)block[2 + i] << (8 * i);

        var values = new byte[BlockSide * BlockSide];
        for (var i = 0; i < values.Length; i++) values[i] = palette[(int)((indices >> (3 * i)) & 7)];
        return values;
    }

    private static ColorRgba32[] Decode(ReadOnlySpan<byte> blocks, int width, int height, int channels)
    {
        var blocksAcross = (width + BlockSide - 1) / BlockSide;
        var blocksDown = (height + BlockSide - 1) / BlockSide;
        var blockBytes = ChannelBlockBytes * channels;
        if (blocks.Length < (long)blocksAcross * blocksDown * blockBytes) throw new InvalidDataException("A DDS level ends before its pixel data does");

        var pixels = new ColorRgba32[width * height];
        for (var by = 0; by < blocksDown; by++)
        for (var bx = 0; bx < blocksAcross; bx++)
        {
            var block = blocks.Slice((by * blocksAcross + bx) * blockBytes, blockBytes);
            var red = DecodeChannelBlock(block[..ChannelBlockBytes]);
            var green = channels == 2 ? DecodeChannelBlock(block[ChannelBlockBytes..]) : null;
            for (var y = 0; y < BlockSide && by * BlockSide + y < height; y++)
            for (var x = 0; x < BlockSide && bx * BlockSide + x < width; x++)
            {
                var i = y * BlockSide + x;
                pixels[(by * BlockSide + y) * width + bx * BlockSide + x] = new ColorRgba32(red[i], green?[i] ?? 0, 0, 255);
            }
        }

        return pixels;
    }

    private static byte[] Palette(ReadOnlySpan<byte> block)
    {
        // -128 and -127 both mean -1.
        double r0 = Math.Max((sbyte)block[0] / 127.0, -1);
        double r1 = Math.Max((sbyte)block[1] / 127.0, -1);

        var values = new double[8];
        values[0] = r0;
        values[1] = r1;
        if ((sbyte)block[0] > (sbyte)block[1])
        {
            for (var i = 2; i < 8; i++) values[i] = ((8 - i) * r0 + (i - 1) * r1) / 7;
        }
        else
        {
            for (var i = 2; i < 6; i++) values[i] = ((6 - i) * r0 + (i - 1) * r1) / 5;
            values[6] = -1;
            values[7] = 1;
        }

        var palette = new byte[8];
        for (var i = 0; i < 8; i++) palette[i] = (byte)Math.Round((values[i] * 0.5 + 0.5) * 255);
        return palette;
    }
}
