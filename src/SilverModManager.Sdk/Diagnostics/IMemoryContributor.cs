namespace SilverModManager.Sdk.Diagnostics;

/// <summary>
/// A native allocator or library that can say how much of the process's memory it holds. The managed GC cannot see these
/// bytes, so a contributor reports its own counters and the install log prints them beside the anonymous total.
/// </summary>
public interface IMemoryContributor
{
    string Name { get; }

    /// <summary>The counts by key, in bytes. Empty when the contributor cannot read them.</summary>
    IReadOnlyList<(string Key, long Bytes)> Sample();

    /// <summary>Free text printed once after the counts, such as the name of the library the process mapped. Null for none.</summary>
    string? Note => null;
}
