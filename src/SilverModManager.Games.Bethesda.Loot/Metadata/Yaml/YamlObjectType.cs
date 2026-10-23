namespace SilverModManager.Games.Bethesda.Loot.Metadata.Yaml;

/// <summary>
/// The kind of metadata object being read, named in parse errors. Follows libloot's metadata/yaml/parse.rs.
/// </summary>
internal enum YamlObjectType
{
    File,
    Group,
    Location,
    Message,
    MessageContent,
    PluginCleaningData,
    PluginMetadata,
    Tag,
    MetadataDocument,
    BashTagsElement,
}

/// <summary>
/// The kind of value a parse error expected. Follows libloot's metadata/error.rs.
/// </summary>
internal enum ExpectedType
{
    String,
    Number,
    Array,
    Map,
    MapOrString,
    ArrayOrString,
}

internal static class YamlTypeNames
{
    public static string Display(this YamlObjectType type) => type switch
    {
        YamlObjectType.File => "file",
        YamlObjectType.Group => "group",
        YamlObjectType.Location => "location",
        YamlObjectType.Message => "message",
        YamlObjectType.MessageContent => "message content",
        YamlObjectType.PluginCleaningData => "plugin cleaning data",
        YamlObjectType.PluginMetadata => "plugin metadata",
        YamlObjectType.Tag => "tag",
        YamlObjectType.MetadataDocument => "metadata document",
        YamlObjectType.BashTagsElement => "bash tags",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    public static string Display(this ExpectedType type) => type switch
    {
        ExpectedType.String => "a string",
        ExpectedType.Number => "a number",
        ExpectedType.Array => "an array",
        ExpectedType.Map => "a map",
        ExpectedType.MapOrString => "a map or string",
        ExpectedType.ArrayOrString => "an array or string",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
