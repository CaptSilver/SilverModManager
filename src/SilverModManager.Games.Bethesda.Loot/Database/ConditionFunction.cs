namespace NexusMods.Games.Bethesda.Loot.Database;

// Follows loot-condition-interpreter's function/mod.rs (Function, ComparisonOperator).

public enum ComparisonOperator
{
    Equal,
    NotEqual,
    LessThan,
    GreaterThan,
    LessThanOrEqual,
    GreaterThanOrEqual,
}

/// <summary>
/// One function call in a condition, with its arguments as written. Paths keep their text, and
/// patterns are the regex text as the interpreter compiles it, so a <c>file</c>, <c>active</c>,
/// <c>many</c>, <c>many_active</c> or <c>filename_version</c> pattern includes its <c>^</c> and <c>$</c>
/// anchors. Every pattern is known to compile and to avoid the constructs Rust's regex crate rejects.
/// </summary>
public abstract record ConditionFunction
{
    private ConditionFunction() { }

    public sealed record FilePath(string Path) : ConditionFunction;

    /// <param name="ParentPath">The directory the pattern is matched in, <c>.</c> if the condition gave none.</param>
    public sealed record FileRegex(string ParentPath, string Pattern) : ConditionFunction;

    public sealed record FileSize(string Path, ulong Size) : ConditionFunction;

    public sealed record Readable(string Path) : ConditionFunction;

    public sealed record IsExecutable(string Path) : ConditionFunction;

    public sealed record ActivePath(string Path) : ConditionFunction;

    public sealed record ActiveRegex(string Pattern) : ConditionFunction;

    public sealed record IsMaster(string Path) : ConditionFunction;

    public sealed record Many(string ParentPath, string Pattern) : ConditionFunction;

    public sealed record ManyActive(string Pattern) : ConditionFunction;

    public sealed record Checksum(string Path, uint Crc) : ConditionFunction;

    public sealed record Version(string Path, string Value, ComparisonOperator Comparator) : ConditionFunction;

    public sealed record ProductVersion(string Path, string Value, ComparisonOperator Comparator) : ConditionFunction;

    /// <param name="Pattern">Has exactly one capture group, which holds the version.</param>
    public sealed record FilenameVersion(string ParentPath, string Pattern, string Value, ComparisonOperator Comparator) : ConditionFunction;

    /// <param name="Pattern">Matched anywhere in the description, not anchored.</param>
    public sealed record DescriptionContains(string Path, string Pattern) : ConditionFunction;
}
