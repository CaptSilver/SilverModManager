namespace NexusMods.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/vertex.rs.

/// <summary>The kinds of interaction between plugins or groups.</summary>
public enum EdgeType
{
    Hardcoded,
    MasterFlag,
    Master,
    MasterlistRequirement,
    UserRequirement,
    MasterlistLoadAfter,
    UserLoadAfter,
    MasterlistGroup,
    UserGroup,
    RecordOverlap,
    AssetOverlap,
    TieBreak,
    BlueprintMaster,
}

public static class EdgeTypeExtensions
{
    /// <summary>The text libloot shows for the edge type.</summary>
    public static string ToDisplayString(this EdgeType edgeType) => edgeType switch
    {
        EdgeType.Hardcoded => "Hardcoded",
        EdgeType.MasterFlag => "Master Flag",
        EdgeType.Master => "Master",
        EdgeType.MasterlistRequirement => "Masterlist Requirement",
        EdgeType.UserRequirement => "User Requirement",
        EdgeType.MasterlistLoadAfter => "Masterlist Load After",
        EdgeType.UserLoadAfter => "User Load After",
        EdgeType.MasterlistGroup => "Masterlist Group",
        EdgeType.UserGroup => "User Group",
        EdgeType.RecordOverlap => "Record Overlap",
        EdgeType.AssetOverlap => "Asset Overlap",
        EdgeType.TieBreak => "Tie Break",
        EdgeType.BlueprintMaster => "Blueprint Master",
        _ => throw new ArgumentOutOfRangeException(nameof(edgeType), edgeType, null),
    };
}
