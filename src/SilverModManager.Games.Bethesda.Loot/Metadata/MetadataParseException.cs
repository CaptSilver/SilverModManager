using SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;

namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Thrown when YAML cannot be read as metadata: malformed YAML, or a value of the wrong type, a
/// missing key or a duplicate entry. Follows libloot's <c>ParseMetadataError</c>.
/// </summary>
public sealed class MetadataParseException : Exception
{
    private readonly int _zeroBasedColumn;

    internal MetadataParseException(SourcePosition position, string reason, Exception? inner = null, bool isWithinPrelude = false)
        : base(reason, inner)
    {
        Line = position.Line;
        _zeroBasedColumn = position.Column;
        Reason = reason;
        IsWithinPrelude = isWithinPrelude;
    }

    /// <summary>The 1-based line of the problem, in the file the user can open.</summary>
    public int Line { get; }

    /// <summary>The 1-based column of the problem.</summary>
    public int Column => _zeroBasedColumn + 1;

    /// <summary>What is wrong, without the location.</summary>
    public string Reason { get; }

    /// <summary>True if <see cref="Line"/> counts lines of the prelude file rather than of the masterlist.</summary>
    public bool IsWithinPrelude { get; }

    public override string Message => IsWithinPrelude
        ? $"encountered a YAML parsing error at line {Line} column {Column} within the prelude: {Reason}"
        : $"encountered a YAML parsing error at line {Line} column {Column}: {Reason}";

    internal MetadataParseException Adjusted(PreludeDiffSpan diff)
    {
        var (position, isWithinPrelude) = diff.Adjust(new SourcePosition(Line, _zeroBasedColumn));
        return new MetadataParseException(position, Reason, InnerException, isWithinPrelude);
    }

    internal static MetadataParseException MissingKey(SourcePosition position, string key, YamlObjectType type) =>
        new(position, $"\"{key}\" key in \"{type.Display()}\" map is missing");

    internal static MetadataParseException DuplicateEntry(SourcePosition position, string id, YamlObjectType type) =>
        new(position, $"more than one entry exists for {type.Display()} \"{id}\"");

    internal static MetadataParseException UnexpectedType(SourcePosition position, YamlObjectType type, ExpectedType expected) =>
        new(position, $"\"{type.Display()}\" object must be {expected.Display()}");

    internal static MetadataParseException UnexpectedValueType(SourcePosition position, string key, YamlObjectType type, ExpectedType expected) =>
        new(position, $"\"{key}\" key in \"{type.Display()}\" map must be {expected.Display()}");

    internal static MetadataParseException NonU32Number(SourcePosition position, long number) =>
        new(position, $"{number} is not valid as a 32-bit unsigned integer");

    internal static MetadataParseException InvalidRegex(SourcePosition position, string name, Exception inner) =>
        new(position, $"invalid regex in \"name\" key with value \"{name}\"", inner);

    internal static MetadataParseException InvalidCondition(SourcePosition position, string condition, Exception inner) =>
        new(position, $"the condition string \"{condition}\" is invalid", inner);
}
