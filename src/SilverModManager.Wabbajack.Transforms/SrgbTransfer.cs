namespace SilverModManager.Wabbajack.Transforms;

/// <summary>
/// The sRGB transfer function as two 256-entry tables over 8-bit channels. A texture whose recorded format differs
/// from its source in sRGB-ness gets its colour channels mapped through one of them; alpha is never touched.
/// </summary>
internal static class SrgbTransfer
{
    /// <summary>Linear value to sRGB-encoded value.</summary>
    public static readonly byte[] ToSrgb = Build(Encode);

    /// <summary>sRGB-encoded value to linear value.</summary>
    public static readonly byte[] ToLinear = Build(Decode);

    /// <summary>The linear value, in 0..1, of each sRGB-encoded byte.</summary>
    public static readonly double[] LinearValues = Enumerable.Range(0, 256).Select(i => Decode(i / 255.0)).ToArray();

    // Linear values where the nearest sRGB byte changes: byte v owns [Boundaries[v - 1], Boundaries[v]).
    private static readonly double[] Boundaries = Enumerable.Range(0, 255).Select(i => Decode((i + 0.5) / 255.0)).ToArray();

    /// <summary>
    /// The sRGB byte nearest to a linear value in 0..1 (clamped), found by searching the byte boundaries so it agrees
    /// exactly with rounding the encoded value, without a <c>Math.Pow</c> per channel.
    /// </summary>
    public static byte EncodeLinear(double linear)
    {
        var at = Array.BinarySearch(Boundaries, linear);
        return (byte)(at >= 0 ? at + 1 : ~at);
    }

    /// <summary>Maps the colour channels of tightly packed RGBA pixels through <paramref name="table"/>, in place.</summary>
    public static void Apply(Span<byte> rgba, byte[] table)
    {
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = table[rgba[i]];
            rgba[i + 1] = table[rgba[i + 1]];
            rgba[i + 2] = table[rgba[i + 2]];
        }
    }

    private static byte[] Build(Func<double, double> transfer)
    {
        var table = new byte[256];
        for (var i = 0; i < table.Length; i++)
            table[i] = (byte)Math.Clamp(Math.Round(transfer(i / 255.0) * 255.0), 0, 255);
        return table;
    }

    private static double Encode(double linear)
        => linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;

    private static double Decode(double encoded)
        => encoded <= 0.04045 ? encoded / 12.92 : Math.Pow((encoded + 0.055) / 1.055, 2.4);
}
