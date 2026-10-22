namespace NexusMods.Games.Bethesda.Loot.Metadata;

/// <summary>
/// A group that plugin metadata can belong to. Follows libloot's metadata/group.rs.
/// </summary>
public sealed record Group(string Name)
{
    /// <summary>The group every plugin belongs to unless its metadata says otherwise.</summary>
    public const string DefaultName = "default";

    public Group() : this(DefaultName) { }

    public string? Description { get; init; }

    /// <summary>The names of the groups this group loads after.</summary>
    public EquatableArray<string> AfterGroups { get; init; }
}
