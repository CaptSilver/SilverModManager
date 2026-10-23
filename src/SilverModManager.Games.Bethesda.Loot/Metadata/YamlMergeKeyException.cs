using SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;

namespace SilverModManager.Games.Bethesda.Loot.Metadata;

/// <summary>
/// Thrown when a <c>&lt;&lt;</c> merge key's value is neither a mapping nor a sequence of mappings.
/// Follows libloot's <c>YamlMergeKeyError</c>.
/// </summary>
public sealed class YamlMergeKeyException : Exception
{
    private readonly int _zeroBasedColumn;

    internal YamlMergeKeyException(SourcePosition position, string value, bool isWithinPrelude = false)
    {
        Line = position.Line;
        _zeroBasedColumn = position.Column;
        Value = value;
        IsWithinPrelude = isWithinPrelude;
    }

    public int Line { get; }

    public int Column => _zeroBasedColumn + 1;

    /// <summary>The offending value, written as YAML.</summary>
    public string Value { get; }

    public bool IsWithinPrelude { get; }

    public override string Message => IsWithinPrelude
        ? $"invalid YAML merge key value at line {Line} column {Column} within the prelude: {Value}"
        : $"invalid YAML merge key value at line {Line} column {Column}: {Value}";

    internal YamlMergeKeyException Adjusted(PreludeDiffSpan diff)
    {
        var (position, isWithinPrelude) = diff.Adjust(new SourcePosition(Line, _zeroBasedColumn));
        return new YamlMergeKeyException(position, Value, isWithinPrelude);
    }
}
