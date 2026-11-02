using SilverModManager.Wabbajack.Transforms;

namespace SilverModManager.Wabbajack.Transforms.Tests;

public class Bc4SnormDecoderTests
{
    /// <summary>Packs sixteen 3-bit indices into the six index bytes of a BC4 block.</summary>
    private static byte[] Block(sbyte first, sbyte second, params int[] indices)
    {
        var bits = 0UL;
        for (var i = 0; i < indices.Length; i++) bits |= (ulong)indices[i] << (3 * i);
        var block = new byte[8];
        block[0] = (byte)first;
        block[1] = (byte)second;
        for (var i = 0; i < 6; i++) block[2 + i] = (byte)(bits >> (8 * i));
        return block;
    }

    [Fact]
    public void FirstEndpointBelowTheSecondUsesFourInterpolantsAndTheExtremes()
    {
        // -127 > 127 is false, so indices 6 and 7 are -1 and +1 and 2..5 are the fifths between the endpoints.
        var values = Bc4SnormDecoder.DecodeChannelBlock(Block(-127, 127, 0, 1, 2, 3, 4, 5, 6, 7));

        Assert.Equal(new byte[] { 0, 255, 51, 102, 153, 204, 0, 255 }, values[..8]);
    }

    [Fact]
    public void FirstEndpointAboveTheSecondUsesSixInterpolants()
    {
        var values = Bc4SnormDecoder.DecodeChannelBlock(Block(127, -127, 0, 1, 2, 3, 4, 5, 6, 7));

        Assert.Equal(new byte[] { 255, 0, 219, 182, 146, 109, 73, 36 }, values[..8]);
    }

    [Fact]
    public void TheMinusOneEndpointsMeanTheSameAsMinus127()
        => Assert.Equal(Bc4SnormDecoder.DecodeChannelBlock(Block(-127, 127, 0, 1, 2)), Bc4SnormDecoder.DecodeChannelBlock(Block(-128, 127, 0, 1, 2)));

    [Fact]
    public void ABc4LevelFillsRedAndLeavesGreenAndBlueEmpty()
    {
        var pixels = Bc4SnormDecoder.DecodeBc4(Block(-127, 127, 1), 4, 4);

        Assert.All(pixels[..1], p => Assert.Equal(new BCnEncoder.Shared.ColorRgba32(255, 0, 0, 255), p));
        Assert.Equal(16, pixels.Length);
    }

    [Fact]
    public void ABc5LevelDecodesRedFromTheFirstBlockAndGreenFromTheSecond()
    {
        byte[] blocks = [.. Block(-127, 127, 1), .. Block(-127, 127, 0)];

        var pixel = Bc4SnormDecoder.DecodeBc5(blocks, 4, 4)[0];

        Assert.Equal(new BCnEncoder.Shared.ColorRgba32(255, 0, 0, 255), pixel);
    }

    [Fact]
    public void ALevelNarrowerThanABlockKeepsOnlyItsPixels()
    {
        var pixels = Bc4SnormDecoder.DecodeBc4(Block(-127, 127), 2, 3);

        Assert.Equal(6, pixels.Length);
    }

    [Fact]
    public void ADataRunShorterThanTheLevelIsRefused()
        => Assert.Throws<InvalidDataException>(() => Bc4SnormDecoder.DecodeBc4(new byte[4], 4, 4));
}
