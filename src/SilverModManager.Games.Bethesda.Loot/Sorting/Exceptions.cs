namespace SilverModManager.Games.Bethesda.Loot.Sorting;

// Follows libloot's sorting/error.rs.

/// <summary>Base of the errors raised while sorting plugins.</summary>
public class SortingException : Exception
{
    public SortingException(string message) : base(message) { }

    public SortingException(string message, Exception? inner) : base(message, inner) { }
}

/// <summary>Thrown when a group name does not match any group in the graph.</summary>
public sealed class UndefinedGroupException(string groupName) : SortingException($"the group \"{groupName}\" does not exist")
{
    public string GroupName { get; } = groupName;
}

/// <summary>Thrown when interactions form a cycle, with the vertices that make up the cycle.</summary>
public sealed class CyclicInteractionException(IReadOnlyList<Vertex> cycle)
    : SortingException($"cyclic interaction detected: {DisplayCycle(cycle)}")
{
    public IReadOnlyList<Vertex> Cycle { get; } = cycle;

    /// <summary>The cycle as <c>a --[Type]-> b --[Type]-> a</c>, repeating the first vertex at the end.</summary>
    public static string DisplayCycle(IReadOnlyList<Vertex> cycle)
    {
        var text = new System.Text.StringBuilder();
        foreach (var vertex in cycle)
        {
            text.Append(vertex.Name);
            if (vertex.OutEdgeType is { } edgeType) text.Append(" --[").Append(edgeType.ToDisplayString()).Append("]-> ");
        }

        if (cycle.Count > 0) text.Append(cycle[0].Name);
        return text.ToString();
    }
}

public enum PathfindingExceptionKind
{
    NegativeCycle,
    PrecedingNodeNotFound,
    FollowingNodeNotFound,
    EdgeNotFound,
}

/// <summary>Thrown when a path search finds something that should be impossible in a valid graph.</summary>
public sealed class PathfindingException : SortingException
{
    private PathfindingException(PathfindingExceptionKind kind, string message, string? fromGroup = null, string? toGroup = null)
        : base(message)
    {
        Kind = kind;
        FromGroup = fromGroup;
        ToGroup = toGroup;
    }

    public PathfindingExceptionKind Kind { get; }

    /// <summary>The node the message is about, or the start of the missing edge.</summary>
    public string? FromGroup { get; }

    /// <summary>The end of the missing edge for <see cref="PathfindingExceptionKind.EdgeNotFound"/>.</summary>
    public string? ToGroup { get; }

    public static PathfindingException NegativeCycle() => new(
        PathfindingExceptionKind.NegativeCycle,
        "encountered a cycle of user-defined \"load after\" relationships");

    public static PathfindingException PrecedingNodeNotFound(string node) => new(
        PathfindingExceptionKind.PrecedingNodeNotFound,
        $"unexpectedly could not find the node before \"{node}\" in the path that was found",
        fromGroup: node);

    public static PathfindingException FollowingNodeNotFound(string node) => new(
        PathfindingExceptionKind.FollowingNodeNotFound,
        $"unexpectedly could not find the node after \"{node}\" in the path that was found",
        fromGroup: node);

    public static PathfindingException EdgeNotFound(string fromGroup, string toGroup) => new(
        PathfindingExceptionKind.EdgeNotFound,
        $"unexpectedly could not find the edge going from \"{fromGroup}\" to \"{toGroup}\"",
        fromGroup,
        toGroup);
}

/// <summary>Thrown when the plugin graph's specific or early-loading edges contradict plugin master flags.</summary>
public sealed class PluginGraphValidationException(CyclicInteractionException cycle)
    : SortingException("found a cycle in the plugin graph", cycle)
{
    public CyclicInteractionException Cycle { get; } = cycle;
}

/// <summary>Thrown when the topological sort of the plugin graph meets a cycle.</summary>
public sealed class CycleInvolvingPluginException(string pluginName)
    : SortingException($"found a cycle involving \"{pluginName}\"")
{
    public string PluginName { get; } = pluginName;
}

/// <summary>
/// Thrown when building the groups graph fails: a group that is not defined, or a cycle of
/// "load after" relationships. Exactly one of <see cref="UndefinedGroup"/> and <see cref="Cycle"/> is set.
/// </summary>
public sealed class BuildGroupsGraphException : Exception
{
    public BuildGroupsGraphException(UndefinedGroupException undefinedGroup)
        : base("encountered an undefined group", undefinedGroup) => UndefinedGroup = undefinedGroup;

    public BuildGroupsGraphException(CyclicInteractionException cycle)
        : base("the groups graph is cyclic", cycle) => Cycle = cycle;

    public UndefinedGroupException? UndefinedGroup { get; }

    public CyclicInteractionException? Cycle { get; }
}

public enum GroupsPathExceptionKind
{
    UndefinedGroup,
    CycleFound,
    Pathfinding,
}

/// <summary>Thrown when the path between two groups cannot be worked out.</summary>
public sealed class GroupsPathException : Exception
{
    private GroupsPathException(GroupsPathExceptionKind kind, string message, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
    }

    public GroupsPathException(UndefinedGroupException undefinedGroup)
        : this(GroupsPathExceptionKind.UndefinedGroup, undefinedGroup.Message, undefinedGroup)
    {
        GroupName = undefinedGroup.GroupName;
    }

    public GroupsPathException(PathfindingException pathfinding)
        : this(GroupsPathExceptionKind.Pathfinding, "failed to find a path in the groups graph", pathfinding) { }

    public GroupsPathException(CyclicInteractionException cycle)
        : this(GroupsPathExceptionKind.CycleFound, $"found a cycle: {CyclicInteractionException.DisplayCycle(cycle.Cycle)}", cycle)
    {
        Cycle = cycle.Cycle;
    }

    public GroupsPathExceptionKind Kind { get; }

    /// <summary>The missing group, for <see cref="GroupsPathExceptionKind.UndefinedGroup"/>.</summary>
    public string? GroupName { get; }

    /// <summary>The cycle, for <see cref="GroupsPathExceptionKind.CycleFound"/>.</summary>
    public IReadOnlyList<Vertex>? Cycle { get; }

    public static GroupsPathException FromBuildError(BuildGroupsGraphException error) => error switch
    {
        { UndefinedGroup: { } undefined } => new GroupsPathException(undefined),
        { Cycle: { } cycle } => new GroupsPathException(cycle),
        _ => throw new ArgumentException("The build error has neither an undefined group nor a cycle.", nameof(error)),
    };
}
