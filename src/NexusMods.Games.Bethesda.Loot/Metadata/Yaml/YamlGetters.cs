using NexusMods.Games.Bethesda.Loot.Metadata;
using YamlDotNet.RepresentationModel;

namespace NexusMods.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// Typed access to YAML mappings, with libloot's error wording. Follows libloot's metadata/yaml/parse.rs.
/// </summary>
internal static class YamlGetters
{
    public static SourcePosition PositionOf(YamlNode node) => SourcePosition.From(node.Start);

    public static YamlNode? GetValue(YamlMappingNode mapping, string key)
    {
        foreach (var (candidate, value) in mapping.Children)
        {
            if (candidate is YamlScalarNode scalar && scalar.Value == key) return value;
        }

        return null;
    }

    public static (SourcePosition Position, string Value)? GetString(YamlMappingNode mapping, string key, YamlObjectType type)
    {
        var node = GetValue(mapping, key);
        if (node is null) return null;

        var value = CoreSchema.AsString(node)
            ?? throw MetadataParseException.UnexpectedValueType(PositionOf(node), key, type, ExpectedType.String);
        return (PositionOf(node), value);
    }

    /// <param name="position">Where to report the key as missing: the start of the enclosing map.</param>
    public static string GetRequiredString(SourcePosition position, YamlMappingNode mapping, string key, YamlObjectType type)
    {
        var value = GetString(mapping, key, type);
        return value?.Value ?? throw MetadataParseException.MissingKey(position, key, type);
    }

    public static List<string> GetStrings(YamlMappingNode mapping, string key, YamlObjectType type)
    {
        var node = GetValue(mapping, key);
        if (node is null) return [];

        if (node is not YamlSequenceNode sequence)
            throw MetadataParseException.UnexpectedValueType(PositionOf(node), key, type, ExpectedType.Array);

        var strings = new List<string>(sequence.Children.Count);
        foreach (var element in sequence.Children)
        {
            strings.Add(CoreSchema.AsString(element)
                ?? throw MetadataParseException.UnexpectedValueType(PositionOf(element), key, type, ExpectedType.String));
        }

        return strings;
    }

    public static YamlMappingNode AsMapping(YamlNode node, YamlObjectType type)
    {
        return node as YamlMappingNode ?? throw MetadataParseException.UnexpectedType(PositionOf(node), type, ExpectedType.Map);
    }

    /// <summary>
    /// A 32-bit unsigned integer. Like saphyr, only a plain scalar can be a number: <c>0x12AB</c>,
    /// <c>4779</c> and <c>0o11253</c> are, <c>'0x12AB'</c> is a string.
    /// </summary>
    public static uint? GetUInt32(YamlMappingNode mapping, string key, YamlObjectType type)
    {
        var node = GetValue(mapping, key);
        if (node is null) return null;

        if (node is not YamlScalarNode scalar || CoreSchema.Classify(scalar, out var integer) != ScalarKind.Integer)
            throw MetadataParseException.UnexpectedValueType(PositionOf(node), key, type, ExpectedType.Number);

        if (integer is < 0 or > uint.MaxValue) throw MetadataParseException.NonU32Number(PositionOf(node), integer);

        return (uint)integer;
    }

    public static IList<YamlNode> GetSlice(YamlMappingNode mapping, string key, YamlObjectType type)
    {
        var node = GetValue(mapping, key);
        if (node is null) return [];

        return node is YamlSequenceNode sequence
            ? sequence.Children
            : throw MetadataParseException.UnexpectedValueType(PositionOf(node), key, type, ExpectedType.Array);
    }

    /// <summary>
    /// A condition or constraint string. libloot also parses it here and rejects one that is not a
    /// valid condition expression; this keeps the text as written.
    /// </summary>
    public static string? ReadCondition(YamlMappingNode mapping, string key, YamlObjectType type) =>
        GetString(mapping, key, type)?.Value;
}
