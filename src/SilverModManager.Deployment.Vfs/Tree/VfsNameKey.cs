namespace NexusMods.Deployment.Vfs;

/// <summary>
/// The key a directory uses to find a child. Equality is exactly
/// <c>string.Equals(a, b, StringComparison.OrdinalIgnoreCase)</c> with no Unicode normalisation,
/// which is how <c>RelativePath</c> compares. The synchronizer's keys and the tree's can then never
/// disagree about which names are one file. It delegates to the comparer instead of folding by hand
/// because surrogate pairs and casing-table edge cases must come out the same.
/// </summary>
internal readonly struct VfsNameKey : IEquatable<VfsNameKey>
{
    public string Name { get; }

    public VfsNameKey(string name) => Name = name;

    public bool Equals(VfsNameKey other) => StringComparer.OrdinalIgnoreCase.Equals(Name, other.Name);
    public override bool Equals(object? obj) => obj is VfsNameKey other && Equals(other);
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
    public override string ToString() => Name;
}
