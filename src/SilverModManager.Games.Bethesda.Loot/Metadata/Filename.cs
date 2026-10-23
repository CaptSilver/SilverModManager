namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A case-insensitive filename. Follows libloot's metadata/file.rs.
/// </summary>
/// <remarks>
/// libloot compares with <c>unicase</c> (Unicode case folding); this uses ordinal case-insensitive
/// comparison, which agrees for every name a plugin or data file can have in practice.
/// </remarks>
public readonly struct Filename : IEquatable<Filename>, IComparable<Filename>
{
    private readonly string? _value;

    public Filename(string value) => _value = value;

    public string Value => _value ?? "";

    public bool Equals(Filename other) => string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is Filename other && Equals(other);

    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Value);

    public int CompareTo(Filename other) => StringComparer.OrdinalIgnoreCase.Compare(Value, other.Value);

    public override string ToString() => Value;

    public static bool operator ==(Filename left, Filename right) => left.Equals(right);

    public static bool operator !=(Filename left, Filename right) => !left.Equals(right);

    public static bool operator <(Filename left, Filename right) => left.CompareTo(right) < 0;

    public static bool operator >(Filename left, Filename right) => left.CompareTo(right) > 0;

    public static bool operator <=(Filename left, Filename right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Filename left, Filename right) => left.CompareTo(right) >= 0;
}
