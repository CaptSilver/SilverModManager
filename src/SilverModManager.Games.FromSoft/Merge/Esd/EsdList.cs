using System.Collections;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// An immutable list that compares by content. The canonical forms are records nested several levels deep, and a
/// record holding a plain list would compare it by reference and report two identical machines as different.
/// </summary>
public sealed class EsdList<T>(IEnumerable<T> items) : IReadOnlyList<T>, IEquatable<EsdList<T>>
{
    private readonly T[] _items = items.ToArray();

    public static EsdList<T> Empty { get; } = new([]);

    public int Count => _items.Length;
    public T this[int index] => _items[index];

    public bool Equals(EsdList<T>? other) => other is not null && _items.AsSpan().SequenceEqual(other._items);
    public override bool Equals(object? obj) => obj is EsdList<T> other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items) hash.Add(item);
        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => "[" + string.Join(", ", _items) + "]";
}
