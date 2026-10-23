namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/vertex.rs.

/// <summary>
/// A plugin or group in a path, and the type of the edge to the next vertex in the path if there
/// is one.
/// </summary>
public sealed record Vertex(string Name, EdgeType? OutEdgeType = null)
{
    public Vertex WithOutEdgeType(EdgeType outEdgeType) => this with { OutEdgeType = outEdgeType };
}
