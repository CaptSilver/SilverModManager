namespace SilverModManager.Platform.Proton.Steam.BinaryKeyValues;

/// <summary>
/// One node of a binary KeyValues file.
/// </summary>
public abstract record BinaryKv(string Name)
{
    /// <summary>
    /// Whether two nodes hold the same names, types and values all the way down. Record equality cannot say so
    /// because a map's children are a list.
    /// </summary>
    public static bool SameTree(BinaryKv? a, BinaryKv? b) => (a, b) switch
    {
        (null, null) => true,
        (BinaryKvMap x, BinaryKvMap y) => x.Name == y.Name && x.Children.Count == y.Children.Count
                                          && x.Children.Zip(y.Children).All(pair => SameTree(pair.First, pair.Second)),
        (BinaryKvString x, BinaryKvString y) => x == y,
        (BinaryKvInt32 x, BinaryKvInt32 y) => x == y,
        _ => false,
    };
}

/// <summary>A map (type byte 0x00).</summary>
public sealed record BinaryKvMap(string Name, List<BinaryKv> Children) : BinaryKv(Name)
{
    public BinaryKv? Find(string name) => Children.FirstOrDefault(c => c.Name == name);
}

/// <summary>A string (type byte 0x01).</summary>
public sealed record BinaryKvString(string Name, string Value) : BinaryKv(Name);

/// <summary>A little-endian 32-bit integer (type byte 0x02).</summary>
public sealed record BinaryKvInt32(string Name, int Value) : BinaryKv(Name);
